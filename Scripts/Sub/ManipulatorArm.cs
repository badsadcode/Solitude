using System;
using Godot;
using Solitude.Core;
using Solitude.Systems;
using Solitude.World;

namespace Solitude.Sub;

/// <summary>
/// Hydraulic manipulator under the bow. Two-bone IK (shoulder yaw + pitch, elbow pitch)
/// driven toward a target point in sub-local space, with servo-rate-limited joints so it
/// moves like heavy machinery. The pilot steers the claw target (mouse = horizontal,
/// wheel / W-S = height); a click closes the claw. If a pickup is within reach of the
/// fingers it is taken, the arm folds back to stow it in the hold, then redeploys.
/// </summary>
public partial class ManipulatorArm : Node3D
{
    public enum ArmState { Stowed, Active, Returning }

    public TerrainStreamer Terrain;
    public TerrainGenerator Gen;
    public ShipSystems Systems;
    public Action<float> OnClaw; // claw clack / impact sounds

    public const float L1 = 3.2f, L2 = 3.0f, ClawLen = 0.7f;
    public static readonly Vector3 Shoulder = new(0.9f, -3.0f, -9.8f);

    public ArmState State { get; private set; } = ArmState.Stowed;
    public Vector3 Target;                       // sub-local claw target
    public Camera3D Camera { get; private set; }
    public float ServoActivity { get; private set; }
    public TerrainStreamer.Pickup Nearest { get; private set; }
    public float NearestDistance { get; private set; }
    public ItemId? Held { get; private set; }
    public bool Deployed => State != ArmState.Stowed;

    private Node3D _yaw, _upper, _fore, _wrist, _fingerL, _fingerR;
    private MeshInstance3D _heldMesh;
    private SpotLight3D _light;
    private float _yawA, _a1 = 0.05f, _a2 = -(Mathf.Pi - 0.05f);
    private float _claw = 1f, _clawTarget = 1f;   // 1 = open
    private float _clawTimer;
    private bool _wantStow;
    private Vector3 _camLook;

    private const float ServoRate = 1.1f;        // rad/s
    private const float StowA1 = 0.05f, StowA2 = -(Mathf.Pi - 0.05f);
    private static readonly Vector3 DeployTarget = Shoulder + new Vector3(0, -2.6f, -3.6f);

    public override void _Ready()
    {
        var steel = new Color(0.4f, 0.42f, 0.38f);
        var dark = new Color(0.14f, 0.15f, 0.15f);
        var brass = new Color(0.55f, 0.45f, 0.25f);

        _yaw = new Node3D { Position = Shoulder, Name = "ArmYaw" };
        AddChild(_yaw);
        _yaw.AddChild(Mesh(b => { b.Cylinder(new Vector3(0, -0.1f, 0), 0.35f, 0.3f, 0.35f, 8, brass.Shade(0.8f)); }));

        _upper = new Node3D { Name = "Upper" };
        _yaw.AddChild(_upper);
        _upper.AddChild(Mesh(b =>
        {
            b.Box(new Vector3(0, 0, -L1 / 2f), new Vector3(0.3f, 0.32f, L1), steel);
            b.Box(new Vector3(0, 0.22f, -L1 * 0.45f), new Vector3(0.12f, 0.12f, L1 * 0.7f), dark); // hydraulic ram
            b.Box(new Vector3(0, 0, 0), new Vector3(0.42f, 0.42f, 0.42f), dark);
        }));

        _fore = new Node3D { Position = new Vector3(0, 0, -L1), Name = "Fore" };
        _upper.AddChild(_fore);
        _fore.AddChild(Mesh(b =>
        {
            b.Box(new Vector3(0, 0, -L2 / 2f), new Vector3(0.24f, 0.26f, L2), steel.Shade(0.9f));
            b.Box(new Vector3(0, 0, 0), new Vector3(0.34f, 0.34f, 0.34f), dark);
            b.Box(new Vector3(0, -0.18f, -L2 * 0.5f), new Vector3(0.08f, 0.08f, L2 * 0.6f), dark);
        }));

        _wrist = new Node3D { Position = new Vector3(0, 0, -L2), Name = "Wrist" };
        _fore.AddChild(_wrist);
        _wrist.AddChild(Mesh(b => b.Box(new Vector3(0, 0, -0.12f), new Vector3(0.3f, 0.3f, 0.25f), brass)));
        _fingerL = new Node3D { Position = new Vector3(-0.1f, 0, -0.22f) };
        _fingerR = new Node3D { Position = new Vector3(0.1f, 0, -0.22f) };
        _wrist.AddChild(_fingerL);
        _wrist.AddChild(_fingerR);
        foreach (var (f, s) in new[] { (_fingerL, -1f), (_fingerR, 1f) })
            f.AddChild(Mesh(b =>
            {
                b.Box(new Vector3(0, 0, -0.22f), new Vector3(0.07f, 0.12f, 0.45f), dark);
                b.BoxOriented(new Transform3D(new Basis(Vector3.Up, -s * 0.7f), new Vector3(-s * 0.06f, 0, -0.48f)), new Vector3(0.06f, 0.1f, 0.2f), dark);
            }));

        _heldMesh = new MeshInstance3D { Position = new Vector3(0, -0.1f, -0.65f), Visible = false, Scale = Vector3.One * 0.8f };
        _wrist.AddChild(_heldMesh);

        _light = new SpotLight3D
        {
            Position = new Vector3(0, 0.25f, -0.1f),
            Rotation = new Vector3(-0.3f, 0, 0),
            SpotRange = 16f,
            SpotAngle = 40f,
            LightEnergy = 4f,
            LightColor = new Color(1f, 0.92f, 0.8f),
            LightVolumetricFogEnergy = 1.2f,
            Visible = false,
        };
        _wrist.AddChild(_light);

        Camera = new Camera3D { Name = "ArmCam", Fov = 72f, Near = 0.1f, Far = 120f, Position = Shoulder + new Vector3(-0.9f, 0.3f, 1.6f) };
        AddChild(Camera);
        _camLook = DeployTarget;
        Target = DeployTarget;
        ApplyPose();
    }

    private static MeshInstance3D Mesh(Action<MeshBuilder> build)
    {
        var mb = new MeshBuilder();
        build(mb);
        return new MeshInstance3D { Mesh = mb.Build(Art.Hull) };
    }

    // ------------------------------------------------------------------ control API

    public void Deploy()
    {
        if (State != ArmState.Stowed) return;
        State = ArmState.Active;
        _wantStow = false;
        Target = DeployTarget;
        _clawTarget = 1f;
        _light.Visible = true;
        if (Systems != null) Systems.ArmActive = true;
    }

    public void Stow()
    {
        if (State == ArmState.Stowed) return;
        _wantStow = true;
        State = ArmState.Returning;
    }

    /// <summary>Move the claw target. dx/dz horizontal (sub-local metres), dy height.</summary>
    public void Steer(float dx, float dz, float dy)
    {
        if (State != ArmState.Active) return;
        Target += new Vector3(dx, dy, dz);
        Vector3 d = Target - Shoulder;
        if (d.Length() > L1 + L2 - 0.15f) d = d.Normalized() * (L1 + L2 - 0.15f);
        if (d.Length() < 1.6f) d = d.Normalized() * 1.6f;
        d.Y = Mathf.Min(d.Y, -0.4f);
        d.Z = Mathf.Min(d.Z, 1.0f);
        Target = Shoulder + d;
    }

    /// <summary>Close the claw. Returns true if something was grabbed.</summary>
    public bool Grab()
    {
        if (State != ArmState.Active || _clawTimer > 0f) return false;
        _clawTarget = 0f;
        _clawTimer = 0.6f;
        var tip = TipWorld();
        var pk = Terrain?.NearestPickup(tip, 1.4f);
        if (pk != null)
        {
            Terrain.Take(pk);
            Held = pk.Type;
            _heldMesh.Mesh = Props.Pickups[pk.Type];
            _heldMesh.Visible = true;
            State = ArmState.Returning;
            OnClaw?.Invoke(0.5f);
            return true;
        }
        OnClaw?.Invoke(0.2f);
        return false;
    }

    public Vector3 TipWorld() => _wrist.GlobalTransform * new Vector3(0, 0, -0.55f);

    // ------------------------------------------------------------------ simulation

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var sub = GetParent<Submarine>();

        // Keep the claw out of the seabed: lift the target if it would dig in.
        if (State == ArmState.Active && Gen != null)
        {
            Vector3 tw = sub.GlobalTransform * Target;
            float floor = Gen.Height(tw.X, tw.Z) + 0.35f;
            if (tw.Y < floor)
            {
                tw.Y = floor;
                Target = sub.GlobalTransform.AffineInverse() * tw;
            }
            // Dragging a deployed arm at speed is how you lose it.
            if (Mathf.Abs(sub.ForwardSpeed) > 2.6f) Stow();
        }

        // Desired joint angles
        float yawT, a1T, a2T;
        if (State == ArmState.Returning)
        {
            yawT = 0f; a1T = StowA1; a2T = StowA2;
        }
        else if (State == ArmState.Active)
        {
            Solve(Target, out yawT, out a1T, out a2T);
        }
        else
        {
            yawT = 0f; a1T = StowA1; a2T = StowA2;
        }

        float before = Mathf.Abs(_yawA) + Mathf.Abs(_a1) + Mathf.Abs(_a2);
        float step = ServoRate * dt;
        _yawA = Mathf.MoveToward(_yawA, yawT, step);
        _a1 = Mathf.MoveToward(_a1, a1T, step);
        _a2 = Mathf.MoveToward(_a2, a2T, step * 1.3f);
        float moved = Mathf.Abs(Mathf.Abs(_yawA) + Mathf.Abs(_a1) + Mathf.Abs(_a2) - before);
        ServoActivity = Mathf.Lerp(ServoActivity, Mathf.Clamp(moved / Mathf.Max(step, 1e-4f), 0f, 1f), 10f * dt);

        // Claw
        _clawTimer = Mathf.Max(0f, _clawTimer - dt);
        if (_clawTimer <= 0f && Held == null) _clawTarget = 1f;
        _claw = Mathf.MoveToward(_claw, _clawTarget, dt * 3f);
        ApplyPose();

        // Reached the stow pose?
        bool atStow = Mathf.Abs(_a1 - StowA1) < 0.02f && Mathf.Abs(_a2 - StowA2) < 0.02f && Mathf.Abs(_yawA) < 0.02f;
        if (State == ArmState.Returning && atStow)
        {
            if (Held is ItemId item)
            {
                Systems?.Collect(item);
                Held = null;
                _heldMesh.Visible = false;
                OnClaw?.Invoke(0.35f);
            }
            if (_wantStow)
            {
                State = ArmState.Stowed;
                _light.Visible = false;
                if (Systems != null) Systems.ArmActive = false;
            }
            else
            {
                State = ArmState.Active; // redeploy toward the last target
            }
        }

        // What's within reach of the fingers right now (for the HUD)
        if (State == ArmState.Active && Terrain != null)
        {
            var tip = TipWorld();
            Nearest = Terrain.NearestPickup(tip, 8f);
            NearestDistance = Nearest != null ? Nearest.Position.DistanceTo(tip) : 99f;
        }
        else Nearest = null;

        // Arm camera tracks the claw lazily
        _camLook = _camLook.Lerp(ToLocal(TipWorld()), Mathf.Clamp(3f * dt, 0f, 1f));
        if (!Camera.Position.IsEqualApprox(_camLook))
            Camera.LookAt(ToGlobal(_camLook), sub.GlobalBasis.Y);
    }

    private void ApplyPose()
    {
        _yaw.Rotation = new Vector3(0, _yawA, 0);
        _upper.Rotation = new Vector3(_a1, 0, 0);
        _fore.Rotation = new Vector3(_a2, 0, 0);
        // Keep the claw roughly pointing down/forward regardless of elbow angle.
        _wrist.Rotation = new Vector3(-(_a1 + _a2) * 0.6f - 0.5f * (State == ArmState.Stowed ? 0f : 1f), 0, 0);
        float open = 0.08f + _claw * 0.45f;
        _fingerL.Rotation = new Vector3(0, open, 0);
        _fingerR.Rotation = new Vector3(0, -open, 0);
    }

    /// <summary>Two-bone IK in the vertical plane through the shoulder, elbow up.</summary>
    private static void Solve(Vector3 target, out float yaw, out float a1, out float a2)
    {
        Vector3 d = target - Shoulder;
        yaw = Mathf.Atan2(-d.X, -d.Z);
        float h = Mathf.Sqrt(d.X * d.X + d.Z * d.Z);
        float v = d.Y;
        float dist = Mathf.Clamp(Mathf.Sqrt(h * h + v * v), Mathf.Abs(L1 - L2) + 0.1f, L1 + L2 - 0.05f);
        float phi = Mathf.Atan2(v, h);
        float cosA = Mathf.Clamp((L1 * L1 + dist * dist - L2 * L2) / (2f * L1 * dist), -1f, 1f);
        float cosB = Mathf.Clamp((L1 * L1 + L2 * L2 - dist * dist) / (2f * L1 * L2), -1f, 1f);
        a1 = phi + Mathf.Acos(cosA);
        a2 = -(Mathf.Pi - Mathf.Acos(cosB));
    }
}
