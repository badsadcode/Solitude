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
    public float WaterFactor = 1f;          // wading through a flooded compartment
    private const float StepHeight = 0.55f;

    private const float WalkSpeed = 2.6f, SprintSpeed = 4.2f, CrouchSpeed = 1.3f, Accel = 10f, Gravity = 9.8f, EyeHeight = 1.62f;

    // Jump & crouch (Tom: "smooth, but not too slow"): ~0.8 m hop, a little heavier on the way down,
    // landing dip proportional to the fall; crouch eases the eyes down over ~0.2 s and really shrinks the body.
    private const float JumpVelocity = 4.0f, FallGravityScale = 1.3f;
    private const float StandHeight = 1.75f, CrouchHeight = 1.15f, CrouchEye = 0.98f;
    public bool Crouching { get; private set; }
    public event Action Jumped;
    public event Action<float> Landed;   // impact speed (m/s)

    private Node3D _tilt, _yaw, _pitch;
    private CollisionShape3D _bodyShape;
    private CapsuleShape3D _capsule;
    private float _bob, _stepDist, _shake, _time, _bobWeight, _stepOffset, _crouch01, _landOffset;
    private bool _wasOnFloor = true;
    private Vector3 _camBase;

    public override void _Ready()
    {
        CollisionLayer = Layers.Player;
        CollisionMask = Layers.Interior;
        FloorMaxAngle = Mathf.DegToRad(50f);

        _capsule = new CapsuleShape3D { Radius = 0.3f, Height = StandHeight };
        _bodyShape = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, StandHeight / 2f, 0) };
        AddChild(_bodyShape);

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

    private void SetCrouch(bool on)
    {
        Crouching = on;
        float h = on ? CrouchHeight : StandHeight;
        _capsule.Height = h;
        _bodyShape.Position = new Vector3(0, h / 2f, 0);
    }

    /// <summary>Is there headroom to stand up from a crouch?</summary>
    private bool CanStand() => !TestMove(GlobalTransform, Vector3.Up * (StandHeight - CrouchHeight + 0.02f));

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

        // Heavier on the way down than on the way up: a hop, not a moon jump.
        if (!IsOnFloor()) v.Y -= Gravity * (v.Y > 0f ? 1f : FallGravityScale) * dt;
        else v.Y = Mathf.Max(v.Y, -0.5f);

        // Crouch (hold): the body shrinks at once (from the feet up); the eyes follow smoothly in _Process.
        bool wantCrouch = InputEnabled && Input.IsActionPressed(GameInput.Crouch);
        if (wantCrouch && !Crouching) SetCrouch(true);
        else if (!wantCrouch && Crouching && CanStand()) SetCrouch(false);

        if (InputEnabled && IsOnFloor() && !Crouching && Input.IsActionJustPressed(GameInput.Jump))
        {
            v.Y = JumpVelocity;
            Jumped?.Invoke();
        }

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
        float speed = (Crouching ? CrouchSpeed : sprint ? SprintSpeed : WalkSpeed) * SpeedFactor * (InStation ? 1f : WaterFactor);
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
        float rise = Stepper.TryStepUp(this, dt, StepHeight);
        _stepOffset -= rise; // body pops up the step; the camera eases after it
        float fallSpeed = -Velocity.Y;
        MoveAndSlide();

        // Landing: a dip in the view proportional to the fall, eased back in _Process.
        bool onFloor = IsOnFloor();
        if (onFloor && !_wasOnFloor && fallSpeed > 1.5f)
        {
            _landOffset = -Mathf.Min(fallSpeed * 0.022f, 0.14f);
            Landed?.Invoke(fallSpeed);
        }
        _wasOnFloor = onFloor;

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

    /// <summary>
    /// Camera feel, tuned after Tom found walking "way too jumpy":
    ///   - shake and vibration are smooth noise (sums of slow sines), never per-frame random jitter;
    ///   - the boat's acceleration only shakes you on real jolts (well above the physics noise),
    ///     and the build-up is per second, not per frame;
    ///   - head bob is subtle (~1.2 cm), and eases in/out with walking speed;
    ///   - stepping up a stair lifts the body instantly but the eyes follow over ~0.15 s.
    /// </summary>
    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;

        if (Sub != null && !InStation)
        {
            // Show only part of the real attitude; full 1:1 is nauseating. Smoothed, so physics
            // micro-wobble never reaches the eyes.
            float pitch = Mathf.DegToRad(Sub.PitchDeg) * 0.55f;
            float roll = Mathf.DegToRad(Sub.RollDeg) * 0.7f;
            _tilt.Rotation = _tilt.Rotation.Lerp(new Vector3(-pitch, 0, -roll), Mathf.Clamp(4f * dt, 0f, 1f));
            float jolt = Mathf.Clamp(Sub.LocalAccel.Length() - 1.5f, 0f, 4f);
            _shake = Mathf.Min(_shake + jolt * 0.25f * dt, 1.2f);
        }
        else _tilt.Rotation = _tilt.Rotation.Lerp(Vector3.Zero, Mathf.Clamp(4f * dt, 0f, 1f));
        _shake = Mathf.MoveToward(_shake, 0f, dt * 1.2f);

        // Smooth shake: two slow noise-like sines per axis instead of a new random value each frame.
        float amp = _shake * _shake * 0.05f;
        float vib = Sub != null && !InStation ? Mathf.Abs(Sub.EngineOutput) * 0.0012f : 0f;
        Vector3 shake = new Vector3(
            Mathf.Sin(_time * 23f) * 0.6f + Mathf.Sin(_time * 37f + 1.3f) * 0.4f,
            Mathf.Sin(_time * 29f + 0.7f) * 0.6f + Mathf.Sin(_time * 41f + 2.1f) * 0.4f, 0) * amp
            + new Vector3(Mathf.Sin(_time * 61f), Mathf.Sin(_time * 53f + 0.4f), 0) * vib;

        // Gentle head bob that fades with speed (no bob standing still, no snap when you stop).
        float speed01 = Mathf.Clamp(new Vector2(Velocity.X, Velocity.Z).Length() / SprintSpeed, 0f, 1f);
        _bobWeight = Mathf.Lerp(_bobWeight, IsOnFloor() ? speed01 : 0f, Mathf.Clamp(6f * dt, 0f, 1f));
        Vector3 bob = new Vector3(Mathf.Cos(_bob * Mathf.Pi * 0.5f) * 0.006f, Mathf.Sin(_bob * Mathf.Pi) * 0.012f, 0) * _bobWeight;

        // Stair steps: the eyes catch up with the body.
        _stepOffset = Mathf.Lerp(_stepOffset, 0f, Mathf.Clamp(14f * dt, 0f, 1f));

        // Crouch: ease the eye height (settles in ~0.2 s; smoothstep so it starts and ends gently).
        _crouch01 = Mathf.MoveToward(_crouch01, Crouching ? 1f : 0f, dt * 5f);
        float c = _crouch01 * _crouch01 * (3f - 2f * _crouch01);
        _tilt.Position = new Vector3(0, Mathf.Lerp(EyeHeight, CrouchEye, c), 0);

        // Landing dip springs back.
        _landOffset = Mathf.Lerp(_landOffset, 0f, Mathf.Clamp(9f * dt, 0f, 1f));

        Camera.Position = _camBase + shake + bob * (1f - c * 0.4f) + new Vector3(0, _stepOffset + _landOffset, 0);
    }
}
