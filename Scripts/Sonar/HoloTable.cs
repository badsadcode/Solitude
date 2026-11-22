using System;
using System.Collections.Generic;
using Godot;
using Solitude.Sub;

namespace Solitude.Sonar;

/// <summary>
/// The sensor bay's holo table: a north-up, sub-centred wireframe of every seabed cell
/// the sonar remembers. Colour = height relative to the keel (amber = at or above it,
/// i.e. you would hit it; cyan → deep blue = below). Cells light up as the ping
/// wavefront reaches them. Overlay: own-ship arrow, depth plumb line, ping ring, compass.
/// Lives in the interior; its origin is the centre of the table top.
/// </summary>
public partial class HoloTable : Node3D
{
    public SonarSystem Sonar;
    public Submarine Sub;
    /// <summary>Known station positions, drawn as beacon pillars.</summary>
    public Func<IEnumerable<Vector3>> Beacons;

    public const float TableRadius = 0.52f;
    public const float BaseHeight = 0.32f;   // projection plane above the table top
    public const float Exaggeration = 1.8f;
    public static readonly float[] Zooms = { 160f, 320f, 520f };
    public int ZoomIndex = 1;
    public float Zoom => Zooms[ZoomIndex];

    private MeshInstance3D _terrain;
    private ImmediateMesh _overlay;
    private ShaderMaterial _mat;
    private OmniLight3D _glow;
    private MeshInstance3D _ownShip;
    private int _builtVersion = -1;
    private Vector3 _builtAt;
    private int _builtZoom = -1;
    private double _nextRebuild;

    private static readonly Color Danger = new(1f, 0.55f, 0.15f);
    private static readonly Color Near = new(0.35f, 1f, 0.95f);
    private static readonly Color Deep = new(0.1f, 0.25f, 1f);

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/holo.gdshader") };
        _terrain = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_terrain);
        _overlay = new ImmediateMesh();
        AddChild(new MeshInstance3D { Mesh = _overlay, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _glow = new OmniLight3D { Position = new Vector3(0, 0.9f, 0), LightColor = new Color(0.3f, 0.9f, 1f), LightEnergy = 0.12f, OmniRange = 2.2f };
        _ownShip = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f, RadialSegments = 10, Rings = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.12f, 0.08f),
                EmissionEnabled = true, Emission = new Color(1f, 0.1f, 0.05f), EmissionEnergyMultiplier = 3f,
                // Depth-tested (it used to ignore depth and showed through every wall in the boat, Tom
                // spotted it). The holo terrain is drawn without depth writes, so the dot still shows
                // inside the wireframe; render it after it.
                RenderPriority = 1,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ownShip);
        AddChild(_glow);
    }

    public void CycleZoom(int dir) => ZoomIndex = Math.Clamp(ZoomIndex + dir, 0, Zooms.Length - 1);

    public override void _Process(double delta)
    {
        if (Sonar == null || Sub == null) return;
        double now = SonarSystem.Now;
        Vector3 sp = Sub.GlobalPosition;

        bool dirty = Sonar.Map.Version != _builtVersion || _builtZoom != ZoomIndex || (sp - _builtAt).Length() > 4f;
        // While a wavefront is travelling, rebuild a few times per second to animate the reveal.
        if (Sonar.WaveActive && now >= _nextRebuild) dirty = true;
        if (dirty)
        {
            Rebuild(sp, now);
            _nextRebuild = now + 0.08;
        }
        DrawOverlay(sp, now);
        _glow.LightEnergy = 0.12f + 0.03f * MathF.Sin((float)now * 9f) + (Sonar.WaveActive ? 0.1f : 0f);
    }

    private float MapY(float h, float subY, float scale)
        => BaseHeight + Math.Clamp((h - subY) * scale * Exaggeration, -0.28f, 0.42f);

    private Color HeightColor(float rel)
    {
        if (rel > -8f) return Danger;
        float t = Math.Clamp((-rel - 8f) / 160f, 0f, 1f);
        return Near.Lerp(Deep, t);
    }

    private void Rebuild(Vector3 sp, double now)
    {
        _builtVersion = Sonar.Map.Version;
        _builtZoom = ZoomIndex;
        _builtAt = sp;

        float scale = TableRadius / Zoom;
        int cr = (int)MathF.Ceiling(Zoom / SonarMap.Cell);
        // Grid stride: ~36 lines across the radius at any zoom; a full-density grid reads as a solid sheet.
        int stride = Math.Max(2, (int)MathF.Round(cr / 36f));
        var c0 = SonarMap.CellOf(sp.X, sp.Z);
        var centre = new Vector2I(c0.X - c0.X % stride, c0.Y - c0.Y % stride); // snap so the grid doesn't swim
        var verts = new List<Vector3>(16384);
        var cols = new List<Color>(16384);

        Vector3 Local(Vector2I cell, float h)
        {
            Vector2 w = SonarMap.CellCentre(cell);
            return new Vector3((w.X - sp.X) * scale, MapY(h, sp.Y, scale), (w.Y - sp.Z) * scale);
        }

        float Fade(Vector3 p, double revealAt)
        {
            float r = new Vector2(p.X, p.Z).Length() / TableRadius;
            float edge = 1f - Math.Clamp((r - 0.8f) / 0.2f, 0f, 1f);
            float age = (float)(now - revealAt);
            float fresh = 1f + 1.5f * MathF.Exp(-MathF.Max(age, 0f) * 1.5f);
            return edge * fresh * 0.4f;
        }

        for (int dz = -cr; dz <= cr; dz += stride)
        {
            for (int dx = -cr; dx <= cr; dx += stride)
            {
                if (dx * dx + dz * dz > cr * cr) continue;
                var cell = new Vector2I(centre.X + dx, centre.Y + dz);
                if (!Sonar.Map.TryGet(cell, out float h, out double t) || t > now) continue;
                Vector3 p = Local(cell, h);
                float a = Fade(p, t);
                if (a <= 0.01f) continue;
                Color col = new(HeightColor(h - sp.Y), a);
                bool linked = false;

                foreach (var n in new[] { new Vector2I(stride, 0), new Vector2I(0, stride) })
                {
                    var nc = cell + n;
                    if (!Sonar.Map.TryGet(nc, out float nh, out double nt) || nt > now) continue;
                    Vector3 q = Local(nc, nh);
                    if (new Vector2(q.X, q.Z).Length() > TableRadius) continue;
                    verts.Add(p); cols.Add(col);
                    verts.Add(q); cols.Add(new Color(HeightColor(nh - sp.Y), Fade(q, nt)));
                    linked = true;
                }
                if (!linked)
                {
                    // Isolated far sample: a small tick so sparse returns still read.
                    verts.Add(p); cols.Add(col);
                    verts.Add(p + new Vector3(0, 0.012f, 0)); cols.Add(col);
                }
            }
        }

        if (verts.Count == 0) { _terrain.Mesh = null; return; }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        _terrain.Mesh = mesh;
    }

    private void DrawOverlay(Vector3 sp, double now)
    {
        _overlay.ClearSurfaces();
        _overlay.SurfaceBegin(Mesh.PrimitiveType.Lines);
        float scale = TableRadius / Zoom;
        float subY = MapY(sp.Y, sp.Y, scale);
        var cyan = new Color(0.4f, 1f, 1f, 0.8f);

        void Line(Vector3 a, Vector3 b, Color c)
        {
            _overlay.SurfaceSetColor(c); _overlay.SurfaceAddVertex(a);
            _overlay.SurfaceSetColor(c); _overlay.SurfaceAddVertex(b);
        }
        void Circle(Vector3 centre, float r, Color c, int seg = 48)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg;
                Line(centre + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * r, centre + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * r, c);
            }
        }

        // Table rim and compass north tick
        Circle(new Vector3(0, 0.01f, 0), TableRadius, new Color(0.3f, 0.9f, 1f, 0.35f), 64);
        Line(new Vector3(0, 0.01f, -TableRadius), new Vector3(0, 0.01f, -TableRadius - 0.06f), cyan);
        Line(new Vector3(-0.02f, 0.01f, -TableRadius - 0.045f), new Vector3(0, 0.01f, -TableRadius - 0.07f), cyan);
        Line(new Vector3(0.02f, 0.01f, -TableRadius - 0.045f), new Vector3(0, 0.01f, -TableRadius - 0.07f), cyan);

        // Own ship: arrow along heading, plumb line to the remembered seabed below
        float hdg = Mathf.DegToRad(Sub.Heading);
        var f = new Vector3(MathF.Sin(hdg), 0, -MathF.Cos(hdg));
        var r = new Vector3(-f.Z, 0, f.X);
        var o = new Vector3(0, subY, 0);
        var white = new Color(1f, 1f, 1f, 0.9f);
        // Tom: "just a red dot" — a solid, pulsing red sphere, plus a short red heading tick.
        float shipPulse = 1f + 0.18f * MathF.Sin((float)now * 5f);
        _ownShip.Position = o;
        _ownShip.Scale = Vector3.One * shipPulse;
        var red = new Color(1f, 0.15f, 0.1f, 1f);
        Line(o + f * 0.02f, o + f * 0.055f, red);
        float below = Sonar.Map.TryGet(SonarMap.CellOf(sp.X, sp.Z), out float gh, out _) ? MapY(gh, sp.Y, scale) : BaseHeight - 0.2f;
        for (float y = subY; y > below; y -= 0.02f)
            Line(new Vector3(0, y, 0), new Vector3(0, MathF.Max(y - 0.01f, below), 0), new Color(white, 0.4f));

        // Travelling ping ring
        if (Sonar.WaveActive)
        {
            Vector3 po = new((Sonar.PingOrigin.X - sp.X) * scale, subY, (Sonar.PingOrigin.Z - sp.Z) * scale);
            float rr = Sonar.FrontRadius * scale;
            if (rr < TableRadius) Circle(po, rr, new Color(0.5f, 1f, 1f, 0.7f), 64);
        }

        // Known stations: a pulsing pillar with a ring at its base (clamped to the rim if far).
        if (Beacons != null)
            foreach (var b in Beacons())
            {
                var p = new Vector3((b.X - sp.X) * scale, 0f, (b.Z - sp.Z) * scale);
                float r2 = new Vector2(p.X, p.Z).Length();
                bool far = r2 > TableRadius * 0.95f;
                if (far) p *= TableRadius * 0.95f / r2;
                float baseY = far ? 0.02f : MapY(b.Y, sp.Y, scale);
                float pulse = 0.6f + 0.4f * MathF.Sin((float)now * 4f);
                var bc = new Color(1f, 0.85f, 0.4f, far ? 0.5f : 0.9f * pulse);
                Line(new Vector3(p.X, baseY, p.Z), new Vector3(p.X, baseY + 0.16f, p.Z), bc);
                Circle(new Vector3(p.X, baseY, p.Z), far ? 0.012f : 0.02f + 0.01f * pulse, bc, 10);
            }

        // Ghost contacts flicker on the table too, briefly
        foreach (var k in Sonar.Contacts)
        {
            if (!k.Ghost || now < k.RevealAt || now - k.RevealAt > 3.0) continue;
            var p = new Vector3((k.World.X - sp.X) * scale, subY, (k.World.Y - sp.Z) * scale);
            if (new Vector2(p.X, p.Z).Length() > TableRadius) continue;
            var g = new Color(1f, 1f, 1f, 0.6f * (float)(1.0 - (now - k.RevealAt) / 3.0));
            Line(p - new Vector3(0.01f, 0, 0), p + new Vector3(0.01f, 0, 0), g);
            Line(p - new Vector3(0, 0, 0.01f), p + new Vector3(0, 0, 0.01f), g);
        }

        _overlay.SurfaceEnd();
    }
}
