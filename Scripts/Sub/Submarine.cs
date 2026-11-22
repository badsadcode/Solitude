using System;
using Godot;
using Solitude.Core;

namespace Solitude.Sub;

public enum DriveMode { Hold, Telegraph }

/// <summary>
/// The boat as a rigid body with a hand-rolled hydrodynamic model:
///  - engine output spools toward the commanded throttle (no instant thrust)
///  - anisotropic drag: slippery fore/aft, very draggy sideways and vertically,
///    so the hull carves turns and keeps drifting after you let go
///  - rudder and dive planes only bite with water flowing over them (scale with speed);
///    small bow thrusters allow a slow pivot when stopped
///  - ballast is a slow tank level, not a button: you plan depth changes ahead
///  - roll self-rights (metacentric), turns induce a lean, pitch is soft-limited
/// Controls are written by HelmController; this class only reads them.
/// </summary>
public partial class Submarine : RigidBody3D
{
    // ---- tuning (mass = 1000 units; numbers are "feel", not naval architecture)
    [Export] public float MaxThrust = 260f;
    [Export] public float ReverseFactor = 0.55f;
    [Export] public float SpoolRate = 0.22f;
    [Export] public float BallastForce = 120f;
    [Export] public float BallastPumpRate = 0.12f;

    private const float DragFwdQ = 7f, DragFwdL = 25f;
    private const float DragSideQ = 90f, DragSideL = 150f;
    private const float DragVertQ = 80f, DragVertL = 150f;
    private const float RudderK = 900f, BowThruster = 900f;
    private const float PlanesK = 900f;
    private const float YawDamp = 30000f, YawDampQ = 60000f;
    private const float PitchRestore = 9000f, PitchDamp = 30000f;
    private const float RollRestore = 25000f, RollDamp = 9000f, RollFromTurn = 4000f;

    // Hull rating (raised by Hull Reinforcement upgrades).
    public float TestDepth = 750f;
    public float CrushDepth = 1050f;

    /// <summary>Flood array upgrade: longer, brighter beams.</summary>
    public void SetFloodBoost(float mult)
    {
        for (int i = 0; i < 2 && _floods != null; i++) { _floods[i].SpotRange = 95f * mult; _floods[i].LightEnergy = 11f * (0.7f + 0.3f * mult); }
    }

    // ---- controls (written by the helm)
    public float TargetThrottle;           // -1..1
    public float Rudder;                   // -1..1, + = starboard (right)
    public float Planes;                   // -1..1, + = nose up
    public float BallastInput;             // -1 flood .. +1 blow (pump direction while held)
    public DriveMode Mode = DriveMode.Telegraph;
    public int TelegraphIndex = 2;         // into Telegraph[]
    public bool LightsOn = true;
    /// <summary>Max |engine output| the power bus allows (1 normally, low on emergency power).</summary>
    public float EngineLimit = 1f;

    public static readonly (string name, float throttle)[] Telegraph =
    {
        ("FULL ASTERN", -1f), ("HALF ASTERN", -0.5f), ("ALL STOP", 0f),
        ("AHEAD SLOW", 0.33f), ("AHEAD HALF", 0.66f), ("AHEAD FULL", 1f),
    };

    // ---- state (read by HUD, audio, interior)
    public float EngineOutput { get; private set; }
    public float Ballast { get; private set; }               // -1..1, 0 = neutral
    public float Hull { get; private set; } = 100f;
    public float Depth => Mathf.Max(0f, -GlobalPosition.Y);
    public float ForwardSpeed { get; private set; }
    public float VerticalSpeed => LinearVelocity.Y;
    public float Heading { get; private set; }              // degrees, 0 = -Z ("north")
    public float PitchDeg { get; private set; }
    public float RollDeg { get; private set; }
    public float YawRate { get; private set; }
    public Vector3 LocalAccel { get; private set; }
    public float Stress => Mathf.Clamp((Depth - TestDepth) / (CrushDepth - TestDepth), 0f, 1.5f);

    /// <summary>Raised on collisions with the impact speed change (m/s).</summary>
    public event Action<float> Impact;

    private Vector3 _prevVel;
    private Node3D _prop;
    private SpotLight3D[] _floods;
    private OmniLight3D _beacon;
    private float _beaconT;
    private float _impactCooldown;

    public override void _Ready()
    {
        Mass = 1000f;
        GravityScale = 0f;
        CanSleep = false;
        ContactMonitor = true;
        MaxContactsReported = 4;
        LinearDampMode = DampMode.Replace;
        AngularDampMode = DampMode.Replace;
        LinearDamp = 0f;
        AngularDamp = 0f;
        Inertia = new Vector3(60000f, 70000f, 6000f);
        CollisionLayer = Layers.Sub;
        CollisionMask = Layers.World;

        // Collision: capsule hull along Z + sail box.
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 2.9f, Height = 32f },
            Rotation = new Vector3(Mathf.Pi / 2f, 0, 0),
        });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.8f, 3f, 6f) }, Position = new Vector3(0, 4.1f, -3.2f) });

        AddChild(new MeshInstance3D { Mesh = SubmarineModel.BuildHull(), Name = "Hull" });
        _prop = new MeshInstance3D { Mesh = SubmarineModel.BuildPropeller(), Position = new Vector3(0, 0, 16.6f), Name = "Propeller" };
        AddChild(_prop);

        // Floodlights: the only real light down here.
        _floods = new[]
        {
            MakeSpot(new Vector3(-1.5f, -1.0f, -15.6f), new Vector3(-0.22f, 0.06f, 0), 95f, 30f, 11f, true),
            MakeSpot(new Vector3(1.5f, -1.0f, -15.6f), new Vector3(-0.22f, -0.06f, 0), 95f, 30f, 11f, true),
            MakeSpot(new Vector3(0, -3.2f, -5.5f), new Vector3(-Mathf.Pi / 2f + 0.25f, 0, 0), 65f, 55f, 6f, false),
        };

        AddChild(new OmniLight3D { Position = new Vector3(-3.3f, 0.2f, -2f), LightColor = new Color(1f, 0.1f, 0.05f), LightEnergy = 1.2f, OmniRange = 4f });
        AddChild(new OmniLight3D { Position = new Vector3(3.3f, 0.2f, -2f), LightColor = new Color(0.1f, 1f, 0.25f), LightEnergy = 1.2f, OmniRange = 4f });
        _beacon = new OmniLight3D { Position = new Vector3(0, 7.5f, -2.2f), LightColor = new Color(1f, 0.95f, 0.8f), LightEnergy = 0f, OmniRange = 9f };
        AddChild(_beacon);
        // Faint spill from the portholes.
        AddChild(new OmniLight3D { Position = new Vector3(-3.6f, 0.75f, -2f), LightColor = new Color(1f, 0.75f, 0.4f), LightEnergy = 0.4f, OmniRange = 6f });
        AddChild(new OmniLight3D { Position = new Vector3(3.6f, 0.75f, -2f), LightColor = new Color(1f, 0.75f, 0.4f), LightEnergy = 0.4f, OmniRange = 6f });

        _prevVel = LinearVelocity;
    }

    private SpotLight3D MakeSpot(Vector3 pos, Vector3 rot, float range, float angle, float energy, bool shadows)
    {
        var s = new SpotLight3D
        {
            Position = pos,
            Rotation = rot,
            SpotRange = range,
            SpotAngle = angle,
            SpotAttenuation = 1.1f,
            SpotAngleAttenuation = 0.8f,
            LightEnergy = energy,
            LightColor = new Color(0.95f, 0.97f, 1f),
            LightVolumetricFogEnergy = 1.6f,
            ShadowEnabled = shadows,
        };
        AddChild(s);
        return s;
    }

    public void ApplyTelegraph()
    {
        TelegraphIndex = Math.Clamp(TelegraphIndex, 0, Telegraph.Length - 1);
        TargetThrottle = Telegraph[TelegraphIndex].throttle;
    }

    public void Repair(float amount) => Hull = Mathf.Min(100f, Hull + amount);
    public void SetHull(float hull) => Hull = Mathf.Clamp(hull, 0f, 100f);

    public void Damage(float amount)
    {
        Hull = Mathf.Max(0f, Hull - amount);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Basis b = GlobalBasis.Orthonormalized();
        Vector3 right = b.X, up = b.Y, fwd = -b.Z;
        Vector3 v = LinearVelocity;

        // Engine spools slowly; reversing is weaker.
        EngineOutput = Mathf.MoveToward(EngineOutput, TargetThrottle, SpoolRate * dt);
        float limited = Mathf.Clamp(EngineOutput, -EngineLimit, EngineLimit);
        float thrust = limited * MaxThrust * (limited >= 0f ? 1f : ReverseFactor);

        // Ballast tank level follows the pumps.
        Ballast = Mathf.Clamp(Ballast + BallastInput * BallastPumpRate * dt, -1f, 1f);

        float vf = v.Dot(fwd), vr = v.Dot(right), vu = v.Dot(up);
        ForwardSpeed = vf;

        Vector3 force = fwd * thrust;
        force -= fwd * (vf * Mathf.Abs(vf) * DragFwdQ + vf * DragFwdL);
        force -= right * (vr * Mathf.Abs(vr) * DragSideQ + vr * DragSideL);
        force -= up * (vu * Mathf.Abs(vu) * DragVertQ + vu * DragVertL);
        force += Vector3.Up * Ballast * BallastForce;
        ApplyCentralForce(force);

        // Torques in local space: x = pitch (+ nose up), y = yaw (+ left), z = roll (+ right side up)
        Vector3 w = b.Transposed() * AngularVelocity;
        float pitch = Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f));
        float roll = Mathf.Asin(Mathf.Clamp(right.Y, -1f, 1f));

        float lowSpeed = 1f - Mathf.Clamp(Mathf.Abs(vf) / 3f, 0f, 1f);
        float yawT = -Rudder * vf * RudderK - Rudder * BowThruster * lowSpeed;
        yawT -= w.Y * YawDamp + w.Y * Mathf.Abs(w.Y) * YawDampQ;

        float pitchT = Planes * vf * PlanesK - pitch * PitchRestore - w.X * PitchDamp;
        float limit = Mathf.DegToRad(35f);
        if (Mathf.Abs(pitch) > limit) pitchT -= (pitch - Mathf.Sign(pitch) * limit) * 80000f;

        float rollT = -roll * RollRestore - w.Z * RollDamp + w.Y * vf * RollFromTurn;
        ApplyTorque(b * new Vector3(pitchT, yawT, rollT));

        // Telemetry
        PitchDeg = Mathf.RadToDeg(pitch);
        RollDeg = Mathf.RadToDeg(roll);
        YawRate = Mathf.RadToDeg(w.Y);
        Vector3 flat = new Vector3(fwd.X, 0, fwd.Z);
        if (flat.LengthSquared() > 1e-4f)
            Heading = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(flat.X, -flat.Z)), 360f);

        // Impacts: a velocity change much bigger than our own forces can cause, while touching.
        Vector3 dv = v - _prevVel;
        LocalAccel = b.Transposed() * (dv / Mathf.Max(dt, 1e-4f));
        _impactCooldown -= dt;
        if (GetContactCount() > 0 && dv.Length() > 0.45f && _impactCooldown <= 0f)
        {
            float strength = dv.Length();
            if (strength > 0.9f) Damage((strength - 0.8f) * 6f);
            Impact?.Invoke(strength);
            _impactCooldown = 0.35f;
        }
        _prevVel = v;

        // Beyond crush depth the hull slowly gives up.
        if (Depth > CrushDepth) Damage((Depth - CrushDepth) * 0.004f * dt);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _prop.RotateZ(EngineOutput * 9f * dt);

        foreach (var s in _floods) s.Visible = LightsOn;

        _beaconT += dt;
        float blink = Mathf.PosMod(_beaconT, 2.6f);
        _beacon.LightEnergy = blink < 0.08f || (blink > 0.25f && blink < 0.33f) ? 2.5f : 0f;
    }
}
