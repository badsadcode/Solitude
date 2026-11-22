using System;
using Godot;

namespace Solitude.Sub;

/// <summary>
/// Real cameras on the hull, each rendering the actual world into its own low-res
/// SubViewport. The textures feed the CRTs in the sensor bay, the bridge screen and the
/// fullscreen overlay. To keep it cheap, feeds refresh round-robin (choppy CCTV framerate,
/// which is also exactly the right vibe) and only "focused" feeds update every frame.
/// </summary>
public partial class HullCameraRig : Node
{
    public sealed class Feed
    {
        public string Name;
        public SubViewport Viewport;
        public Camera3D Camera;
        public Vector3 MountPos;
        public Vector3 MountDir;
        public bool Panning;
        public float Signal = 0.9f;
        public bool Focused;
        public float DipTimer;
        // Pan / tilt / zoom (an idea from BALE IX): offsets from the mount, driven from the monitor desk.
        public float Yaw, Pitch, Zoom = 1f, ZoomShown = 1f, BaseFov;
        /// <summary>Mast camera only: seconds left before it goes back to its own slow sweep.</summary>
        public float ManualT;
        public float MastA, MastUp;
    }

    public const float MaxYaw = 1.25f, MaxPitch = 0.75f, MaxZoom = 4f;
    /// <summary>Servo slew rate at 1x (rad/s). Zoomed in, the head moves slower so the view stays usable.</summary>
    public const float SlewRate = 0.6f;

    public Submarine Sub;
    public Feed[] Feeds { get; private set; }
    public Feed Bridge { get; private set; }

    /// <summary>When false (e.g. player looking through a fullscreen external camera) the monitor feeds stop rendering.</summary>
    public bool MonitorsActive = true;
    public bool BridgeActive = true;

    private static readonly string[] Names = { "CAM 1  BOW", "CAM 2  PORT", "CAM 3  STBD", "CAM 4  KEEL", "CAM 5  STERN", "CAM 6  MAST" };
    private int _rr;
    private float _t;
    private readonly Random _rng = new();

    public override void _Ready()
    {
        Feeds = new Feed[SubmarineModel.CameraPods.Length];
        for (int i = 0; i < Feeds.Length; i++)
        {
            var (pos, dir) = SubmarineModel.CameraPods[i];
            if (i == 3) dir = new Vector3(0, -1f, -0.45f).Normalized();
            if (i == 4) dir = new Vector3(0, -0.3f, 1f).Normalized();
            Feeds[i] = MakeFeed(Names[i], pos + dir * 0.3f, dir, new Vector2I(192, 144), 70f);
            Feeds[i].Panning = i == 5;
        }
        Feeds[5].MastUp = -0.12f;
        var (bp, bd) = SubmarineModel.CameraPods[0];
        Bridge = MakeFeed("BRIDGE", bp + bd * 0.3f, bd, new Vector2I(352, 176), 78f);
    }

    private Feed MakeFeed(string name, Vector3 pos, Vector3 dir, Vector2I size, float fov)
    {
        var vp = new SubViewport
        {
            Name = name.Replace(" ", ""),
            Size = size,
            Msaa3D = Viewport.Msaa.Disabled,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear,
            PositionalShadowAtlasSize = 1024,
        };
        AddChild(vp);
        var cam = new Camera3D { Fov = fov, Near = 0.2f, Far = 220f, Current = true };
        vp.AddChild(cam);
        return new Feed { Name = name, Viewport = vp, Camera = cam, MountPos = pos, MountDir = dir, BaseFov = fov };
    }

    /// <summary>Drive a camera head: yaw/pitch input in -1..1 (right / up positive). Returns true if it actually moved.</summary>
    public bool Aim(int i, float yawIn, float pitchIn, float dt)
    {
        var f = Feeds[i];
        if (yawIn == 0f && pitchIn == 0f) return false;
        float rate = SlewRate / Mathf.Sqrt(f.ZoomShown) * dt;
        if (f.Panning)
        {
            f.ManualT = 25f;
            f.MastA = Mathf.Clamp(f.MastA + yawIn * rate, -2.9f, 2.9f);
            f.MastUp = Mathf.Clamp(f.MastUp + pitchIn * rate * 1.2f, -0.35f, 1.8f);
            return true;
        }
        float y0 = f.Yaw, p0 = f.Pitch;
        f.Yaw = Mathf.Clamp(f.Yaw - yawIn * rate, -MaxYaw, MaxYaw);
        f.Pitch = Mathf.Clamp(f.Pitch + pitchIn * rate, -MaxPitch, MaxPitch);
        return y0 != f.Yaw || p0 != f.Pitch;
    }

    public void ZoomStep(int i, int dir)
    {
        var f = Feeds[i];
        f.Zoom = Mathf.Clamp(dir > 0 ? f.Zoom * 1.4142f : f.Zoom / 1.4142f, 1f, MaxZoom);
        if (f.Zoom < 1.05f) f.Zoom = 1f;
    }

    public void Centre(int i)
    {
        var f = Feeds[i];
        f.Yaw = 0f; f.Pitch = 0f; f.Zoom = 1f; f.ManualT = 0f;
    }

    /// <summary>True while a head is still slewing or zooming toward its target (for the servo whir).</summary>
    public bool Zooming(int i) => Mathf.Abs(Feeds[i].Zoom - Feeds[i].ZoomShown) > 0.02f;

    public string AimText(int i)
    {
        var f = Feeds[i];
        float yawDeg, pitchDeg;
        if (f.Panning) { yawDeg = Mathf.RadToDeg(f.MastA); pitchDeg = Mathf.RadToDeg(Mathf.Atan(f.MastUp)); }
        else { yawDeg = -Mathf.RadToDeg(f.Yaw); pitchDeg = Mathf.RadToDeg(f.Pitch); }
        string auto = f.Panning ? (f.ManualT > 0f ? "  MANUAL" : "  AUTO SWEEP") : "";
        int yd = Mathf.RoundToInt(yawDeg), pd = Mathf.RoundToInt(pitchDeg);
        return $"PAN {yd,4:+0;-0;0}°  TILT {pd,3:+0;-0;0}°  ZOOM {f.ZoomShown:0.0}x{auto}";
    }

    private static Basis LookBasis(Vector3 dir)
    {
        Vector3 up = Mathf.Abs(dir.Y) > 0.95f ? Vector3.Forward : Vector3.Up;
        return Basis.LookingAt(dir, up);
    }

    public override void _Process(double delta)
    {
        if (Sub == null) return;
        float dt = (float)delta;
        _t += dt;
        Transform3D st = Sub.GlobalTransform;

        foreach (var f in Feeds)
        {
            Basis look;
            if (f.Panning)
            {
                float a = Mathf.Sin(_t * 0.12f) * 2.6f;
                // ...and slowly tilts: mostly the horizon, but now and then it looks up into the water
                // above the boat (the only camera that does: that's where the whale passes overhead).
                float up = Mathf.Lerp(-0.12f, 1.6f, Mathf.Max(0f, Mathf.Sin(_t * 0.045f)) * Mathf.Max(0f, Mathf.Sin(_t * 0.045f)));
                if (f.ManualT > 0f) f.ManualT -= dt;
                else
                {
                    // Hands off: drift back into the sweep (a slow servo, never a snap).
                    float k = 1f - Mathf.Exp(-0.6f * dt);
                    f.MastA = Mathf.Lerp(f.MastA, a, k);
                    f.MastUp = Mathf.Lerp(f.MastUp, up, k);
                }
                look = LookBasis(new Vector3(Mathf.Sin(f.MastA), f.MastUp, -Mathf.Cos(f.MastA)).Normalized());
            }
            else look = LookBasis(f.MountDir) * new Basis(Vector3.Up, f.Yaw) * new Basis(Vector3.Right, f.Pitch);
            f.ZoomShown = Mathf.MoveToward(f.ZoomShown, f.Zoom, dt * 1.6f);
            f.Camera.Fov = f.BaseFov / f.ZoomShown;
            f.Camera.GlobalTransform = st * new Transform3D(look, f.MountPos);

            // Signal quality: mostly fine, occasional dips. Deeper = worse.
            f.DipTimer -= dt;
            if (f.DipTimer <= 0f)
            {
                f.DipTimer = 4f + (float)_rng.NextDouble() * 18f;
                bool dip = _rng.NextDouble() < 0.18 + Sub.Stress * 0.3;
                f.Signal = dip ? 0.2f + (float)_rng.NextDouble() * 0.4f : 0.85f + (float)_rng.NextDouble() * 0.15f;
            }
        }
        Bridge.Camera.GlobalTransform = st * new Transform3D(LookBasis(Bridge.MountDir), Bridge.MountPos);

        // Update scheduling.
        _rr = (_rr + 1) % Feeds.Length;
        for (int i = 0; i < Feeds.Length; i++)
        {
            var f = Feeds[i];
            if (!MonitorsActive) f.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            else if (f.Focused) f.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            else if (i == _rr || i == (_rr + 3) % Feeds.Length) f.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            else f.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }
        Bridge.Viewport.RenderTargetUpdateMode = BridgeActive ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }
}
