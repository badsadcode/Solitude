using System;
using Godot;
using Solitude.Core;
using Solitude.Interior;
using Solitude.Sub;

namespace Solitude.Player;

/// <summary>
/// First-person captain. Walks the (static) interior; the boat's attitude is conveyed by
/// a tilt pivot above the camera (the room appears to lean as the hull pitches and rolls),
/// a gentle slide down the "slope", engine vibration and impact shake.
///
/// Rig: Body → Tilt (hull attitude) → Yaw → Pitch → Camera
/// </summary>
public partial class PlayerController : CharacterBody3D
{
    public Submarine Sub;
    public Camera3D Camera { get; private set; }
    public Interactable Focused { get; private set; }
    public bool InputEnabled = true;
    public float MouseSensitivity = 0.0022f;

    public event Action Footstep;
    public event Action<float> Hurt;     // damage taken
    public event Action Died;

    /// <summary>True while aboard a station: no hull tilt, weapons and flashlight allowed.</summary>
    public bool InStation;
    public float Health { get; private set; } = 100f;
    public bool Dead => Health <= 0f;
    public SpotLight3D Flashlight { get; private set; }
    public float SpeedFactor = 1f;          // starving / wounded slows you down
    private const float StepHeight = 0.55f;

    private const float WalkSpeed = 2.6f, SprintSpeed = 4.2f, Accel = 10f, Gravity = 9.8f, EyeHeight = 1.62f;

    private Node3D _tilt, _yaw, _pitch;
    private float _bob, _stepDist, _shake;
    private Vector3 _camBase;

    public override void _Ready()
    {
        CollisionLayer = Layers.Player;
        CollisionMask = Layers.Interior;
        FloorMaxAngle = Mathf.DegToRad(50f);

        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.75f },
            Position = new Vector3(0, 0.875f, 0),
        });

        _tilt = new Node3D { Name = "Tilt", Position = new Vector3(0, EyeHeight, 0) };
        _yaw = new Node3D { Name = "Yaw" };
        _pitch = new Node3D { Name = "Pitch" };
        Camera = new Camera3D { Name = "Eyes", Fov = 72f, Near = 0.05f, Far = 60f };
        AddChild(_tilt);
        _tilt.AddChild(_yaw);
        _yaw.AddChild(_pitch);
        _pitch.AddChild(Camera);
        _camBase = Vector3.Zero;

        Flashlight = new SpotLight3D
        {
            Name = "Flashlight", Position = new Vector3(0.15f, -0.1f, 0), SpotRange = 22f, SpotAngle = 26f,
            SpotAngleAttenuation = 1.4f, LightEnergy = 3.2f, LightColor = new Color(1f, 0.93f, 0.8f),
            ShadowEnabled = true, Visible = false,
        };
        Camera.AddChild(Flashlight);
    }

    public void SetLook(float yaw, float pitch)
    {
        _yaw.Rotation = new Vector3(0, yaw, 0);
        _pitch.Rotation = new Vector3(pitch, 0, 0);
    }

    public void Look(Vector2 rel)
    {
        if (!InputEnabled) return;
        _yaw.RotateY(-rel.X * MouseSensitivity);
        float p = Mathf.Clamp(_pitch.Rotation.X - rel.Y * MouseSensitivity, -1.45f, 1.45f);
        _pitch.Rotation = new Vector3(p, 0, 0);
    }

    public void AddShake(float amount) => _shake = Mathf.Min(_shake + amount, 1.2f);

    public void Damage(float amount)
    {
        if (Dead) return;
        Health = Mathf.Max(0f, Health - amount);
        AddShake(0.4f + amount * 0.02f);
        Hurt?.Invoke(amount);
        if (Dead) Died?.Invoke();
    }

    public void Heal(float amount) => Health = Mathf.Min(100f, Health + amount);
    public void Revive(float health) => Health = health;

    public Vector3 Forward => -Camera.GlobalBasis.Z;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector3 v = Velocity;

        if (!IsOnFloor()) v.Y -= Gravity * dt;
        else v.Y = Mathf.Max(v.Y, -0.5f);

        Vector3 wish = Vector3.Zero;
        bool sprint = false;
        if (InputEnabled)
        {
            Vector2 input = Input.GetVector(GameInput.Left, GameInput.Right, GameInput.Forward, GameInput.Back);
            Basis yb = _yaw.GlobalBasis;
            Vector3 fwd = -yb.Z; fwd.Y = 0; fwd = fwd.Normalized();
            Vector3 right = yb.X; right.Y = 0; right = right.Normalized();
            wish = (right * input.X - fwd * input.Y);
            if (wish.LengthSquared() > 1f) wish = wish.Normalized();
            sprint = Input.IsActionPressed(GameInput.Sprint);
        }
        float speed = (sprint ? SprintSpeed : WalkSpeed) * SpeedFactor;
        Vector3 hv = new(v.X, 0, v.Z);
        hv = hv.Lerp(wish * speed, Mathf.Clamp(Accel * dt, 0f, 1f));

        // The deck leans with the hull: drift downhill.
        if (Sub != null && !InStation && IsOnFloor())
        {
            float roll = Mathf.DegToRad(Sub.RollDeg), pitch = Mathf.DegToRad(Sub.PitchDeg);
            hv += new Vector3(-Mathf.Sin(roll), 0, Mathf.Sin(pitch)) * Gravity * 0.35f * dt;
        }
        v.X = hv.X; v.Z = hv.Z;
        Velocity = v;
        Stepper.TryStepUp(this, dt, StepHeight);
        MoveAndSlide();

        // Footsteps by distance travelled.
        float moved = new Vector2(Velocity.X, Velocity.Z).Length() * dt;
        if (IsOnFloor() && moved > 0.0005f)
        {
            _stepDist += moved;
            _bob += moved * (sprint ? 4.2f : 4.8f);
            if (_stepDist > (sprint ? 0.95f : 0.75f))
            {
                _stepDist = 0f;
                Footstep?.Invoke();
            }
        }

        UpdateFocus();
    }

    private void UpdateFocus()
    {
        Focused = null;
        if (!InputEnabled) return;
        var space = GetWorld3D().DirectSpaceState;
        Vector3 from = Camera.GlobalPosition;
        Vector3 to = from - Camera.GlobalBasis.Z * 2.3f;
        var q = PhysicsRayQueryParameters3D.Create(from, to, Layers.Interior | Layers.Use);
        var hit = space.IntersectRay(q);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is Interactable it) Focused = it;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (Sub != null && InStation)
        {
            _tilt.Rotation = Vector3.Zero;
            _shake = Mathf.MoveToward(_shake, 0f, dt * 1.6f);
            float s0 = _shake * _shake * 0.08f;
            var r0 = new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), 0) * s0;
            float b0 = Mathf.Sin(_bob * Mathf.Pi) * 0.035f;
            Camera.Position = _camBase + r0 + new Vector3(Mathf.Cos(_bob * Mathf.Pi * 0.5f) * 0.02f, b0, 0);
        }
        else if (Sub != null)
        {
            // Show only part of the real attitude; full 1:1 is nauseating.
            float pitch = Mathf.DegToRad(Sub.PitchDeg) * 0.55f;
            float roll = Mathf.DegToRad(Sub.RollDeg) * 0.7f;
            _tilt.Rotation = new Vector3(-pitch, 0, -roll);

            // Engine vibration and surge from acceleration.
            float vib = Mathf.Abs(Sub.EngineOutput) * 0.004f;
            _shake += Mathf.Clamp(Sub.LocalAccel.Length() - 0.4f, 0f, 3f) * 0.02f;
            _shake = Mathf.MoveToward(_shake, 0f, dt * 1.6f);
            float s = _shake * _shake * 0.08f + vib;
            var rng = new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), 0) * s;
            float bob = Mathf.Sin(_bob * Mathf.Pi) * 0.035f;
            Camera.Position = _camBase + rng + new Vector3(Mathf.Cos(_bob * Mathf.Pi * 0.5f) * 0.02f, bob, 0);
        }
    }
}
