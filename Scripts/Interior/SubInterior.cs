using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Sub;

namespace Solitude.Interior;

/// <summary>
/// The walkable inside of the boat. It is built procedurally and lives far above the
/// ocean (static, so walking is rock solid). The hull's motion is conveyed through
/// camera tilt, shake, sliding and sound, not by actually moving the room.
///
/// Layout (local space, bow = -Z, 5 m wide, 2.6 m tall):
///   z -13..-6  BRIDGE       helm, main forward screen, side consoles (red night lighting)
///   z  -6.. 0  SENSOR BAY   camera monitor wall, holo table, sonar (placeholders for M2)
///   z   0.. 5  GALLEY       stove (cooking station, M3), mess table
///   z   5.. 9  QUARTERS     bunks, workbench (crafting station, M3)
///   z   9..13  ENGINEERING  engine block with pistons, dive locker, ceiling hatch (M5)
/// </summary>
public partial class SubInterior : Node3D
{
    public const float HalfW = 2.5f, Height = 2.6f, Bow = -13f, Stern = 13f;

    public Submarine Sub;
    public HullCameraRig Rig;
    public Action OnHelm;
    public Action OnMonitors;
    public Action<string> OnMessage;

    // Exposed anchors (local space)
    public Transform3D PlayerSpawn { get; private set; }
    public Transform3D HelmView { get; private set; }
    public Transform3D MonitorView { get; private set; }

    public ShaderMaterial BridgeScreen { get; private set; }
    public ShaderMaterial BigScreen { get; private set; }
    public readonly List<ShaderMaterial> SmallScreens = new();
    public Label3D BigScreenLabel { get; private set; }
    public Node3D Yoke { get; private set; }

    private StaticBody3D _solid;
    private MeshBuilder _wall, _dark, _floor, _machine, _pipe, _hazard;
    private readonly List<(OmniLight3D light, float baseEnergy, float flicker)> _lights = new();
    private readonly List<MeshInstance3D> _pistons = new();
    private float _t;
    private readonly Random _rng = new();

    private static readonly Color Grey = new(0.85f, 0.85f, 0.85f);
    private static readonly Color Lamp = new(1f, 0.9f, 0.7f, 0.5f);
    private static readonly Color RedLamp = new(1f, 0.2f, 0.15f, 0.5f);
    private static readonly Color GreenLamp = new(0.3f, 1f, 0.4f, 0.5f);
    private static readonly Color AmberLamp = new(1f, 0.6f, 0.1f, 0.5f);

    public void Build()
    {
        _solid = new StaticBody3D { CollisionLayer = Layers.Interior, CollisionMask = 0, Name = "Solid" };
        AddChild(_solid);
        _wall = new MeshBuilder { UvScale = 1f };
        _dark = new MeshBuilder { UvScale = 1f };
        _floor = new MeshBuilder { UvScale = 1f };
        _machine = new MeshBuilder { UvScale = 0.5f };
        _pipe = new MeshBuilder();
        _hazard = new MeshBuilder { UvScale = 0.5f };

        BuildShell();
        BuildBridge();
        BuildSensorBay();
        BuildGalley();
        BuildQuarters();
        BuildEngineering();
        BuildPipes();

        AddChild(new MeshInstance3D { Mesh = _wall.Build(Art.Wall), Name = "Walls" });
        AddChild(new MeshInstance3D { Mesh = _dark.Build(Art.WallDark), Name = "DarkMetal" });
        AddChild(new MeshInstance3D { Mesh = _floor.Build(Art.Floor), Name = "Floor" });
        AddChild(new MeshInstance3D { Mesh = _machine.Build(Art.Machine), Name = "Machines" });
        AddChild(new MeshInstance3D { Mesh = _pipe.Build(Art.Pipe), Name = "Pipes" });
        AddChild(new MeshInstance3D { Mesh = _hazard.Build(Art.Hazard), Name = "Hazard" });

        PlayerSpawn = new Transform3D(Basis.Identity, new Vector3(0, 0.05f, -8.2f));
    }

    // ------------------------------------------------------------------ helpers

    private void Solid(MeshBuilder mb, Vector3 c, Vector3 size, Color col)
    {
        mb.Box(c, size, col);
        _solid.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = c });
    }

    private void Label(string text, Vector3 pos, float yaw, int size = 48, Color? col = null)
    {
        AddChild(new Label3D
        {
            Text = text,
            Position = pos,
            Rotation = new Vector3(0, yaw, 0),
            FontSize = size,
            PixelSize = 0.004f,
            Modulate = col ?? new Color(0.85f, 0.8f, 0.6f),
            OutlineSize = 0,
            Shaded = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        });
    }

    private OmniLight3D Light(Vector3 pos, Color col, float energy, float range, float flicker = 0f, bool shadows = false, bool fixture = true)
    {
        var l = new OmniLight3D
        {
            Position = pos,
            LightColor = col,
            LightEnergy = energy,
            OmniRange = range,
            OmniAttenuation = 1.3f,
            ShadowEnabled = shadows,
        };
        AddChild(l);
        _lights.Add((l, energy, flicker));
        if (!fixture) return l;
        _dark.Box(pos + Vector3.Up * 0.1f, new Vector3(0.5f, 0.06f, 0.3f), Grey);
        _machine.Box(pos + Vector3.Up * 0.05f, new Vector3(0.4f, 0.04f, 0.22f), new Color(col.R, col.G, col.B, 0.5f));
        return l;
    }

    private void Bulkhead(float z, string fore, string aft)
    {
        const float t = 0.2f, doorHalf = 0.55f, doorH = 2.0f;
        float sideW = HalfW - doorHalf;
        Solid(_dark, new Vector3(-(doorHalf + sideW / 2f), Height / 2f, z), new Vector3(sideW, Height, t), Grey);
        Solid(_dark, new Vector3(doorHalf + sideW / 2f, Height / 2f, z), new Vector3(sideW, Height, t), Grey);
        Solid(_dark, new Vector3(0, doorH + (Height - doorH) / 2f, z), new Vector3(doorHalf * 2f, Height - doorH, t), Grey);
        // Heavy frame around the doorway
        _machine.Box(new Vector3(-doorHalf - 0.06f, doorH / 2f, z), new Vector3(0.12f, doorH, 0.34f), Grey);
        _machine.Box(new Vector3(doorHalf + 0.06f, doorH / 2f, z), new Vector3(0.12f, doorH, 0.34f), Grey);
        _hazard.Box(new Vector3(0, doorH + 0.06f, z), new Vector3(doorHalf * 2f + 0.24f, 0.12f, 0.34f), Colors.White);
        _hazard.Box(new Vector3(0, 0.01f, z), new Vector3(doorHalf * 2f, 0.02f, 0.34f), Colors.White);
        // Hatch door swung open against the wall, with a wheel
        _dark.Box(new Vector3(doorHalf + 0.6f, 1.0f, z - 0.16f), new Vector3(1.0f, 1.9f, 0.08f), Grey.Shade(0.8f));
        _pipe.Cylinder(new Vector3(doorHalf + 0.6f, 1.0f, z - 0.24f), 0.2f, 0.2f, 0.04f, 8, Grey);
        if (fore != null) Label(fore, new Vector3(-1.4f, 2.25f, z - t / 2f - 0.01f), Mathf.Pi, 40);
        if (aft != null) Label(aft, new Vector3(1.4f, 2.25f, z + t / 2f + 0.01f), 0f, 40);
    }

    // ------------------------------------------------------------------ shell

    private void BuildShell()
    {
        float len = Stern - Bow;
        float zc = (Bow + Stern) / 2f;
        Solid(_floor, new Vector3(0, -0.1f, zc), new Vector3(HalfW * 2f, 0.2f, len), Grey);
        Solid(_wall, new Vector3(0, Height + 0.1f, zc), new Vector3(HalfW * 2f, 0.2f, len), Grey.Shade(0.8f));
        Solid(_wall, new Vector3(-HalfW - 0.1f, Height / 2f, zc), new Vector3(0.2f, Height, len), Grey);
        Solid(_wall, new Vector3(HalfW + 0.1f, Height / 2f, zc), new Vector3(0.2f, Height, len), Grey);
        Solid(_dark, new Vector3(0, Height / 2f, Bow - 0.1f), new Vector3(HalfW * 2f, Height, 0.2f), Grey);
        Solid(_dark, new Vector3(0, Height / 2f, Stern + 0.1f), new Vector3(HalfW * 2f, Height, 0.2f), Grey);

        // Chamfered upper and lower corners make the cross-section read as a pressure hull.
        foreach (int s in new[] { -1, 1 })
        {
            _wall.BoxOriented(new Transform3D(new Basis(Vector3.Back, s * Mathf.Pi / 4f), new Vector3(s * (HalfW - 0.3f), Height - 0.3f, zc)), new Vector3(0.12f, 0.95f, len), Grey.Shade(0.9f));
            _dark.BoxOriented(new Transform3D(new Basis(Vector3.Back, -s * Mathf.Pi / 4f), new Vector3(s * (HalfW - 0.2f), 0.2f, zc)), new Vector3(0.12f, 0.6f, len), Grey.Shade(0.7f));
        }

        // Ribs (frames) every ~1.6 m
        for (float z = Bow + 0.8f; z < Stern; z += 1.6f)
        {
            foreach (int s in new[] { -1, 1 })
                _dark.Box(new Vector3(s * (HalfW - 0.05f), Height / 2f, z), new Vector3(0.12f, Height, 0.14f), Grey.Shade(0.9f));
            _dark.Box(new Vector3(0, Height - 0.05f, z), new Vector3(HalfW * 2f, 0.1f, 0.14f), Grey.Shade(0.9f));
        }

        Bulkhead(-6f, "BRIDGE", "SENSOR BAY");
        Bulkhead(0f, "SENSOR BAY", "GALLEY");
        Bulkhead(5f, "GALLEY", "QUARTERS");
        Bulkhead(9f, "QUARTERS", "ENGINEERING");
    }

    // ------------------------------------------------------------------ bridge

    private void BuildBridge()
    {
        // Main forward screen (bow camera feed)
        _dark.Box(new Vector3(0, 1.55f, Bow + 0.06f), new Vector3(3.2f, 1.75f, 0.12f), Grey.Shade(0.6f));
        var screen = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2.9f, 1.45f) },
            Position = new Vector3(0, 1.55f, Bow + 0.13f),
        };
        BridgeScreen = Art.Crt(Rig.Bridge.Viewport.GetTexture());
        BridgeScreen.SetShaderParameter("scan_lines", 176f);
        BridgeScreen.SetShaderParameter("desaturate", 0.35f);
        BridgeScreen.SetShaderParameter("brightness", 1.4f);
        screen.MaterialOverride = BridgeScreen;
        AddChild(screen);
        Label("FWD VIEW  -  CAM 1", new Vector3(0, 2.5f, Bow + 0.14f), 0f, 32, new Color(0.6f, 0.9f, 0.7f));

        // Helm console
        Solid(_machine, new Vector3(0, 0.45f, -11.1f), new Vector3(1.6f, 0.9f, 0.7f), Grey);
        _machine.BoxOriented(new Transform3D(new Basis(Vector3.Right, 0.6f), new Vector3(0, 0.98f, -11.05f)), new Vector3(1.6f, 0.06f, 0.75f), Grey.Shade(0.8f));
        for (int i = 0; i < 6; i++)
            _machine.Box(new Vector3(-0.6f + i * 0.24f, 1.02f, -11.15f), new Vector3(0.12f, 0.04f, 0.12f), i % 3 == 0 ? AmberLamp : GreenLamp);
        // Yoke on a column — rotated by the director to show rudder input
        _machine.Box(new Vector3(0, 0.95f, -10.65f), new Vector3(0.12f, 0.2f, 0.4f), Grey.Shade(0.6f));
        Yoke = new Node3D { Position = new Vector3(0, 1.05f, -10.45f), Name = "Yoke" };
        AddChild(Yoke);
        var ymb = new MeshBuilder();
        ymb.Box(new Vector3(0, 0, 0), new Vector3(0.62f, 0.06f, 0.06f), Grey.Shade(0.5f));
        ymb.Box(new Vector3(-0.31f, 0.08f, 0), new Vector3(0.07f, 0.22f, 0.07f), new Color(0.2f, 0.2f, 0.2f));
        ymb.Box(new Vector3(0.31f, 0.08f, 0), new Vector3(0.07f, 0.22f, 0.07f), new Color(0.2f, 0.2f, 0.2f));
        Yoke.AddChild(new MeshInstance3D { Mesh = ymb.Build(Art.Machine) });

        // Engine telegraph pedestal (right of the helm)
        _machine.Box(new Vector3(1.05f, 0.55f, -10.9f), new Vector3(0.3f, 1.1f, 0.3f), Grey.Shade(0.7f));
        _machine.Box(new Vector3(1.05f, 1.2f, -10.9f), new Vector3(0.36f, 0.36f, 0.12f), new Color(0.8f, 0.75f, 0.6f));
        _machine.Box(new Vector3(1.05f, 1.2f, -10.83f), new Vector3(0.05f, 0.26f, 0.02f), RedLamp);

        // Captain's chair
        Solid(_dark, new Vector3(0, 0.25f, -9.75f), new Vector3(0.5f, 0.5f, 0.5f), Grey);
        _dark.Box(new Vector3(0, 0.62f, -9.75f), new Vector3(0.55f, 0.12f, 0.55f), new Color(0.35f, 0.15f, 0.12f));
        _dark.Box(new Vector3(0, 1.0f, -9.5f), new Vector3(0.55f, 0.7f, 0.1f), new Color(0.35f, 0.15f, 0.12f));

        Interactable.Create(this, new Vector3(0, 0.9f, -10.9f), new Vector3(1.6f, 0.8f, 0.9f), "Take the helm", () => OnHelm?.Invoke());
        HelmView = new Transform3D(Basis.Identity, new Vector3(0, 1.45f, -9.65f));

        // Side consoles: ballast board (port), navigation and comms (starboard)
        foreach (int s in new[] { -1, 1 })
        {
            Solid(_machine, new Vector3(s * 2.15f, 0.5f, -10f), new Vector3(0.7f, 1.0f, 3.4f), Grey.Shade(0.9f));
            _machine.BoxOriented(new Transform3D(new Basis(Vector3.Back, s * 0.5f), new Vector3(s * 2.1f, 1.08f, -10f)), new Vector3(0.7f, 0.05f, 3.3f), Grey.Shade(0.7f));
            for (int i = 0; i < 9; i++)
            {
                Color c = (i * 7 + s) % 4 == 0 ? RedLamp : (i % 3 == 0 ? AmberLamp : GreenLamp);
                _machine.Box(new Vector3(s * 2.05f, 1.12f, -11.4f + i * 0.33f), new Vector3(0.08f, 0.05f, 0.08f), c);
            }
            // Dial gauges on the wall
            for (int i = 0; i < 4; i++)
            {
                var dialPos = new Vector3(s * (HalfW - 0.04f), 1.75f, -11.5f + i * 0.9f);
                _machine.Box(dialPos, new Vector3(0.06f, 0.32f, 0.32f), new Color(0.9f, 0.88f, 0.75f));
                _dark.Box(dialPos + new Vector3(-s * 0.04f, 0.04f, 0.02f), new Vector3(0.02f, 0.14f, 0.02f), new Color(0.1f, 0.1f, 0.1f));
            }
        }
        Label("BALLAST", new Vector3(-HalfW + 0.02f, 2.15f, -10f), Mathf.Pi / 2f, 36);
        Label("NAV / COMMS", new Vector3(HalfW - 0.02f, 2.15f, -10f), -Mathf.Pi / 2f, 36);

        Light(new Vector3(0, Height - 0.15f, -9f), new Color(1f, 0.28f, 0.2f), 1.1f, 7f, shadows: true);
        Light(new Vector3(0, Height - 0.15f, -12f), new Color(1f, 0.3f, 0.22f), 0.6f, 4f);
    }

    // ------------------------------------------------------------------ sensor bay

    private void BuildSensorBay()
    {
        float wallX = -HalfW + 0.06f;
        // Monitor wall backing
        _dark.Box(new Vector3(wallX, 1.45f, -3f), new Vector3(0.1f, 1.6f, 4.2f), Grey.Shade(0.5f));

        BigScreen = AddScreen(new Vector3(wallX + 0.08f, 1.45f, -3f), new Vector2(1.36f, 1.02f), -1, 144f);
        BigScreenLabel = new Label3D
        {
            Text = "CAM 1  BOW",
            Position = new Vector3(wallX + 0.09f, 2.08f, -3f),
            Rotation = new Vector3(0, Mathf.Pi / 2f, 0),
            FontSize = 36,
            PixelSize = 0.004f,
            Modulate = new Color(0.6f, 1f, 0.75f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        AddChild(BigScreenLabel);

        float[] cols = { -4.3f, -1.7f };
        float[] rows = { 1.92f, 1.45f, 0.98f };
        int idx = 0;
        foreach (float z in cols)
            foreach (float y in rows)
            {
                var mat = AddScreen(new Vector3(wallX + 0.08f, y, z), new Vector2(0.56f, 0.42f), idx, 96f);
                SmallScreens.Add(mat);
                Label($"{idx + 1}", new Vector3(wallX + 0.09f, y + 0.18f, z + 0.33f), Mathf.Pi / 2f, 24, new Color(0.6f, 1f, 0.75f));
                idx++;
            }

        // Operator desk
        Solid(_machine, new Vector3(-1.95f, 0.42f, -3f), new Vector3(0.7f, 0.84f, 4.0f), Grey.Shade(0.9f));
        _machine.BoxOriented(new Transform3D(new Basis(Vector3.Back, -0.35f), new Vector3(-1.9f, 0.88f, -3f)), new Vector3(0.75f, 0.05f, 4.0f), Grey.Shade(0.7f));
        for (int i = 0; i < 6; i++)
            _machine.Box(new Vector3(-1.85f, 0.92f, -4.3f + i * 0.5f), new Vector3(0.1f, 0.04f, 0.1f), GreenLamp);
        Interactable.Create(this, new Vector3(-1.95f, 0.9f, -3f), new Vector3(0.8f, 0.6f, 4.0f), "Use camera monitors", () => OnMonitors?.Invoke());
        MonitorView = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f) * new Basis(Vector3.Right, 0.04f), new Vector3(0.55f, 1.5f, -3f));
        Solid(_dark, new Vector3(-0.9f, 0.25f, -3f), new Vector3(0.45f, 0.5f, 0.45f), Grey);
        _dark.Box(new Vector3(-0.9f, 0.55f, -3f), new Vector3(0.5f, 0.1f, 0.5f), new Color(0.2f, 0.22f, 0.3f));

        // Holo table (milestone 2)
        Solid(_machine, new Vector3(1.45f, 0.45f, -3.2f), new Vector3(1.1f, 0.9f, 1.1f), Grey.Shade(0.8f));
        _machine.Cylinder(new Vector3(1.45f, 0.9f, -3.2f), 0.62f, 0.62f, 0.06f, 10, Grey.Shade(0.6f));
        _machine.Cylinder(new Vector3(1.45f, 0.96f, -3.2f), 0.5f, 0.5f, 0.01f, 10, new Color(0.1f, 0.4f, 0.45f, 0.6f));
        Interactable.Create(this, new Vector3(1.45f, 0.9f, -3.2f), new Vector3(1.2f, 0.4f, 1.2f), "Holo table",
            () => OnMessage?.Invoke("HOLO TABLE: projector offline. Sonar data feed not connected. (Milestone 2)"));

        // Sonar console on the starboard wall (milestone 2)
        Solid(_machine, new Vector3(2.15f, 0.5f, -0.9f), new Vector3(0.7f, 1.0f, 1.2f), Grey.Shade(0.9f));
        _machine.Box(new Vector3(HalfW - 0.07f, 1.5f, -0.9f), new Vector3(0.08f, 0.6f, 0.6f), new Color(0.05f, 0.25f, 0.12f, 0.7f));
        Interactable.Create(this, new Vector3(2.1f, 1.1f, -0.9f), new Vector3(0.8f, 0.5f, 1.2f), "Sonar",
            () => OnMessage?.Invoke("SONAR: transducer calibration pending. Ping disabled. (Milestone 2)"));
        Label("SONAR", new Vector3(HalfW - 0.02f, 2.0f, -0.9f), -Mathf.Pi / 2f, 36);

        Light(new Vector3(0.6f, Height - 0.15f, -3f), new Color(0.6f, 0.8f, 0.85f), 0.45f, 6f);
        Light(new Vector3(-1.2f, 1.9f, -3f), new Color(0.4f, 1f, 0.7f), 0.35f, 3f, fixture: false);
    }

    private ShaderMaterial AddScreen(Vector3 pos, Vector2 size, int feedIndex, float lines)
    {
        _dark.Box(pos + new Vector3(-0.03f, 0, 0), new Vector3(0.06f, size.Y + 0.08f, size.X + 0.08f), new Color(0.15f, 0.15f, 0.15f));
        Texture2D tex = Rig.Feeds[Math.Max(feedIndex, 0)].Viewport.GetTexture();
        var mat = Art.Crt(tex);
        mat.SetShaderParameter("scan_lines", lines);
        var mi = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = size },
            Position = pos + new Vector3(0.012f, 0, 0),
            Rotation = new Vector3(0, Mathf.Pi / 2f, 0),
            MaterialOverride = mat,
        };
        AddChild(mi);
        if (feedIndex >= 0)
        {
            var hit = new ScreenHit { Index = feedIndex, CollisionLayer = Layers.Interior, CollisionMask = 0, Position = pos };
            hit.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.05f, size.Y, size.X) } });
            AddChild(hit);
        }
        return mat;
    }

    // ------------------------------------------------------------------ galley

    private void BuildGalley()
    {
        // Stove counter (cooking station, milestone 3)
        Solid(_machine, new Vector3(2.1f, 0.45f, 2.5f), new Vector3(0.75f, 0.9f, 2.8f), Grey.Shade(0.95f));
        for (int i = 0; i < 2; i++)
            _dark.Cylinder(new Vector3(2.05f, 0.9f, 1.9f + i * 0.6f), 0.16f, 0.16f, 0.02f, 8, new Color(0.15f, 0.1f, 0.1f));
        _dark.Box(new Vector3(2.15f, 2.1f, 2.2f), new Vector3(0.6f, 0.4f, 1.2f), Grey.Shade(0.8f));
        _wall.Box(new Vector3(2.25f, 1.75f, 3.4f), new Vector3(0.45f, 0.6f, 1.4f), Grey.Shade(0.95f)); // cabinets
        _machine.Box(new Vector3(1.72f, 0.75f, 1.9f), new Vector3(0.04f, 0.06f, 0.06f), RedLamp);
        Interactable.Create(this, new Vector3(2.05f, 1.0f, 2.2f), new Vector3(0.8f, 0.3f, 1.4f), "Galley stove",
            () => OnMessage?.Invoke("GALLEY STOVE: nothing to cook. Your rations are in a crate that doesn't exist yet. (Milestone 3)"));
        Label("GALLEY", new Vector3(HalfW - 0.02f, 2.25f, 4f), -Mathf.Pi / 2f, 36);

        // Mess table, two benches. Seats for four; one is used.
        Solid(_wall, new Vector3(-1.75f, 0.72f, 2.5f), new Vector3(1.0f, 0.06f, 1.8f), Grey.Shade(0.75f));
        _dark.Box(new Vector3(-1.75f, 0.35f, 2.5f), new Vector3(0.12f, 0.7f, 0.12f), Grey);
        Solid(_dark, new Vector3(-1.75f, 0.22f, 1.25f), new Vector3(1.0f, 0.44f, 0.4f), Grey.Shade(0.8f));
        Solid(_dark, new Vector3(-1.75f, 0.22f, 3.75f), new Vector3(1.0f, 0.44f, 0.4f), Grey.Shade(0.8f));
        _machine.Cylinder(new Vector3(-1.6f, 0.75f, 2.2f), 0.05f, 0.045f, 0.1f, 6, new Color(0.7f, 0.68f, 0.6f)); // a mug

        Light(new Vector3(0, Height - 0.15f, 2.5f), new Color(1f, 0.82f, 0.6f), 0.9f, 6f, flicker: 0.6f, shadows: true);
    }

    // ------------------------------------------------------------------ quarters

    private void BuildQuarters()
    {
        // Bunks: three, two of them stripped bare.
        for (int level = 0; level < 2; level++)
        {
            float y = 0.45f + level * 0.95f;
            Solid(_dark, new Vector3(-1.95f, y, 7f), new Vector3(1.0f, 0.12f, 2.3f), Grey.Shade(0.8f));
            if (level == 0)
                _wall.Box(new Vector3(-1.95f, y + 0.1f, 7f), new Vector3(0.9f, 0.1f, 2.1f), new Color(0.35f, 0.38f, 0.32f)); // made bed
        }
        _dark.Box(new Vector3(-1.45f, 1.0f, 5.9f), new Vector3(0.06f, 2.0f, 0.06f), Grey);
        _dark.Box(new Vector3(-1.45f, 1.0f, 8.1f), new Vector3(0.06f, 2.0f, 0.06f), Grey);
        _wall.Box(new Vector3(-2.25f, 2.25f, 7f), new Vector3(0.45f, 0.4f, 2.3f), Grey.Shade(0.9f));

        // Workbench (crafting station, milestone 3)
        Solid(_machine, new Vector3(2.05f, 0.45f, 7f), new Vector3(0.85f, 0.9f, 2.6f), Grey.Shade(0.85f));
        _wall.Box(new Vector3(HalfW - 0.05f, 1.6f, 7f), new Vector3(0.05f, 0.9f, 2.2f), new Color(0.5f, 0.42f, 0.3f)); // pegboard
        for (int i = 0; i < 5; i++)
            _dark.Box(new Vector3(HalfW - 0.1f, 1.4f + (i % 2) * 0.3f, 6.2f + i * 0.4f), new Vector3(0.04f, 0.25f, 0.05f), new Color(0.3f, 0.3f, 0.32f));
        _machine.Box(new Vector3(1.9f, 1.0f, 6.2f), new Vector3(0.25f, 0.2f, 0.3f), Grey.Shade(0.5f)); // vice
        Interactable.Create(this, new Vector3(2.0f, 1.0f, 7f), new Vector3(0.9f, 0.3f, 2.6f), "Workbench",
            () => OnMessage?.Invoke("WORKBENCH: no materials. The manipulator arm is still folded. (Milestone 3)"));
        Label("WORKSHOP", new Vector3(HalfW - 0.02f, 2.25f, 8.3f), -Mathf.Pi / 2f, 36);

        Light(new Vector3(0, Height - 0.15f, 7f), new Color(1f, 0.85f, 0.65f), 0.7f, 5f);
    }

    // ------------------------------------------------------------------ engineering

    private void BuildEngineering()
    {
        // Engine block with pistons, tied to engine output.
        Solid(_machine, new Vector3(1.4f, 0.6f, 11f), new Vector3(1.6f, 1.2f, 3.0f), Grey.Shade(0.75f));
        for (int i = 0; i < 4; i++)
        {
            _machine.Cylinder(new Vector3(1.4f, 1.2f, 9.9f + i * 0.72f), 0.22f, 0.22f, 0.5f, 8, Grey.Shade(0.9f));
            var piston = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f, Height = 0.5f, RadialSegments = 6 },
                Position = new Vector3(1.4f, 1.85f, 9.9f + i * 0.72f),
                MaterialOverride = Art.Pipe,
            };
            AddChild(piston);
            _pistons.Add(piston);
        }
        for (int i = 0; i < 5; i++)
            _machine.Box(new Vector3(0.58f, 0.9f, 10f + i * 0.5f), new Vector3(0.04f, 0.1f, 0.1f), i == 4 ? RedLamp : AmberLamp);
        Label("DRIVE  ENG.  No.1", new Vector3(0.59f, 0.55f, 11f), -Mathf.Pi / 2f, 32);

        // Dive locker with an uncertified suit (milestone 5)
        _dark.Box(new Vector3(-2.15f, 1.2f, 11.6f), new Vector3(0.6f, 2.4f, 1.4f), Grey.Shade(0.6f));
        _wall.Rock(new Vector3(-2.05f, 1.75f, 11.6f), new Vector3(0.2f, 0.22f, 0.2f), new Color(0.75f, 0.6f, 0.25f), 3, 0.05f);
        _wall.Box(new Vector3(-2.05f, 1.15f, 11.6f), new Vector3(0.3f, 0.75f, 0.45f), new Color(0.65f, 0.5f, 0.2f));
        _wall.Box(new Vector3(-2.05f, 0.5f, 11.45f), new Vector3(0.14f, 0.6f, 0.14f), new Color(0.6f, 0.45f, 0.18f));
        _wall.Box(new Vector3(-2.05f, 0.5f, 11.75f), new Vector3(0.14f, 0.6f, 0.14f), new Color(0.6f, 0.45f, 0.18f));
        Interactable.Create(this, new Vector3(-2.0f, 1.2f, 11.6f), new Vector3(0.6f, 2.2f, 1.2f), "Dive suit",
            () => OnMessage?.Invoke("DIVE SUIT: seals uncertified. Airlock cycle untested. You are not going out there. Yet. (Milestone 5)"));
        Label("DIVE LOCKER", new Vector3(-HalfW + 0.02f, 2.3f, 10.3f), Mathf.Pi / 2f, 36);

        // Ladder and ceiling hatch
        for (int i = 0; i < 7; i++)
            _pipe.Box(new Vector3(-0.9f, 0.3f + i * 0.33f, 12.7f), new Vector3(0.5f, 0.04f, 0.04f), Grey);
        _pipe.Box(new Vector3(-1.15f, 1.3f, 12.7f), new Vector3(0.04f, 2.6f, 0.04f), Grey);
        _pipe.Box(new Vector3(-0.65f, 1.3f, 12.7f), new Vector3(0.04f, 2.6f, 0.04f), Grey);
        _hazard.Cylinder(new Vector3(-0.9f, Height - 0.02f, 12.3f), 0.55f, 0.55f, 0.04f, 10, Colors.White);

        Light(new Vector3(0, Height - 0.15f, 11f), new Color(1f, 0.75f, 0.4f), 0.9f, 6f, flicker: 1f, shadows: true);
        Light(new Vector3(-1.5f, Height - 0.3f, 9.3f), new Color(1f, 0.1f, 0.05f), 0.4f, 3f);
    }

    // ------------------------------------------------------------------ pipes

    private void BuildPipes()
    {
        float len = Stern - Bow;
        var pipes = new (float x, float y, float r, Color c)[]
        {
            (-1.55f, 2.42f, 0.07f, new Color(0.6f, 0.62f, 0.58f)),
            (-1.75f, 2.3f, 0.05f, new Color(0.55f, 0.3f, 0.2f)),
            (1.6f, 2.42f, 0.09f, new Color(0.62f, 0.6f, 0.55f)),
            (1.82f, 2.28f, 0.045f, new Color(0.25f, 0.35f, 0.55f)),
            (2.2f, 0.15f, 0.06f, new Color(0.5f, 0.5f, 0.48f)),
        };
        foreach (var p in pipes)
        {
            _pipe.Xform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), new Vector3(p.x, p.y, Bow));
            _pipe.Cylinder(Vector3.Zero, p.r, p.r, len, 6, p.c, caps: false);
        }
        _pipe.Xform = Transform3D.Identity;
        // Flanges and valve wheels
        for (float z = Bow + 1.2f; z < Stern; z += 2.4f)
        {
            _pipe.Box(new Vector3(-1.55f, 2.42f, z), new Vector3(0.2f, 0.2f, 0.06f), Grey.Shade(0.7f));
            _pipe.Box(new Vector3(1.6f, 2.42f, z + 0.6f), new Vector3(0.24f, 0.24f, 0.06f), Grey.Shade(0.7f));
        }
        _hazard.Box(new Vector3(1.6f, 2.18f, -1.7f), new Vector3(0.3f, 0.04f, 0.3f), Colors.White);
    }

    // ------------------------------------------------------------------ runtime

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;

        float stress = Sub?.Stress ?? 0f;
        float hullFrac = (Sub?.Hull ?? 100f) / 100f;
        foreach (var (light, baseE, flicker) in _lights)
        {
            float f = flicker + stress * 0.5f + (1f - hullFrac) * 0.6f;
            if (f > 0f && _rng.NextDouble() < 0.02 * f)
                light.LightEnergy = baseE * (float)(_rng.NextDouble() * 0.4);
            else
                light.LightEnergy = Mathf.Lerp(light.LightEnergy, baseE, 8f * dt);
        }

        float rpm = Mathf.Abs(Sub?.EngineOutput ?? 0f);
        for (int i = 0; i < _pistons.Count; i++)
        {
            var p = _pistons[i].Position;
            p.Y = 1.75f + Mathf.Sin(_t * (2f + rpm * 14f) + i * Mathf.Pi / 2f) * (0.03f + rpm * 0.12f);
            _pistons[i].Position = p;
        }

        if (Yoke != null && Sub != null)
            Yoke.Rotation = new Vector3(Sub.Planes * 0.35f, 0, -Sub.Rudder * 0.9f);
    }
}
