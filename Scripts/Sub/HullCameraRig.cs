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
    }

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
        return new Feed { Name = name, Viewport = vp, Camera = cam, MountPos = pos, MountDir = dir };
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
            Vector3 dir = f.MountDir;
            if (f.Panning)
            {
                float a = Mathf.Sin(_t * 0.12f) * 2.6f;
                dir = new Vector3(Mathf.Sin(a), -0.12f, -Mathf.Cos(a)).Normalized();
            }
            f.Camera.GlobalTransform = st * new Transform3D(LookBasis(dir), f.MountPos);

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
