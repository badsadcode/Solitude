using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Stations;

/// <summary>
/// Station dressing from NoorLevel's station markers (consumer contract: NoorLevel/README.md,
/// "Consumer contract: markers in the .glb"). Only layouts baked with the markers have any of this;
/// older blockouts build exactly as before.
///   prop            → Tom's asset or a placeholder (PropFactory); solid furniture blocks you
///   bulkhead        → a sliding pressure door (jammed half open when the station has no power)
///   viewport        → a porthole onto black water
///   hazard_zone     → flooded: water you wade through slowly / electrified: crackles and hurts (if powered)
///   airlock         → the heavy hatch on the wall behind where you board
///   loot / log_terminal / creature_spawn → preferred spots for loot, crew logs and crawlers
/// The station's style (root extras noor.station.style) tints its walls.
/// </summary>
public partial class StationInterior
{
    private readonly List<Basis> _markerBasis = new();
    private string _style;
    private readonly List<(Aabb box, bool flooded)> _hazards = new();
    private readonly List<SlideDoor> _bulkheads = new();
    private float _zapT;

    /// <summary>A field from a marker: the flat extras first, then extras.noor.</summary>
    private static Variant Field(Godot.Collections.Dictionary extras, string key)
    {
        if (extras.TryGetValue(key, out var v)) return v;
        if (extras.TryGetValue("noor", out var n) && n.VariantType == Variant.Type.Dictionary && n.AsGodotDictionary().TryGetValue(key, out var v2)) return v2;
        return default;
    }

    private static Vector3 Vec3(Variant v, Vector3 fallback)
    {
        if (v.VariantType != Variant.Type.Array) return fallback;
        var a = v.AsGodotArray();
        return a.Count >= 3 ? new Vector3(a[0].AsSingle(), a[2].AsSingle(), a[1].AsSingle()) : fallback;   // [w, d, h] → (x=w, y=h, z=d)
    }

    private static bool HasTag(Godot.Collections.Dictionary extras, string tag)
    {
        var t = Field(extras, "tags");
        if (t.VariantType != Variant.Type.Array) return false;
        foreach (var x in t.AsGodotArray()) if (x.AsString() == tag) return true;
        return false;
    }

    /// <summary>Wall tint per station style (research clean, mining rusty, military olive, derelict dead).</summary>
    private Color StyleTint => _style switch
    {
        "research" => new Color(1.08f, 1.1f, 1.12f),
        "mining" => new Color(1.1f, 0.9f, 0.72f),
        "military" => new Color(0.85f, 0.95f, 0.75f),
        "derelict" => new Color(0.7f, 0.68f, 0.64f),
        _ => Colors.White,
    };

    private readonly Dictionary<Material, Material> _styled = new();
    private Material Styled(Material m)
    {
        if (_style == null || m is not ShaderMaterial sm) return m;
        if (_styled.TryGetValue(m, out var done)) return done;
        var dup = (ShaderMaterial)sm.Duplicate();
        dup.SetShaderParameter("tint", StyleTint);
        _styled[m] = dup;
        return dup;
    }

    private Material _waterMat;
    private Material WaterMaterial()
    {
        if (_waterMat != null) return _waterMat;
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/flood_water.gdshader") };
        m.SetShaderParameter("turbulence", 0.08f);
        m.SetShaderParameter("half_size", new Vector2(999f, 999f));   // no wall foam: we don't know where the walls are
        m.SetShaderParameter("inflow", new Vector2(9999f, 9999f));
        return _waterMat = m;
    }

    // ------------------------------------------------------------------ preferred spots

    private List<(Vector3 p, bool marker)> MarkerSpots(string kind)
    {
        var list = new List<(Vector3, bool)>();
        foreach (var (pos, k, _) in _markers) if (k == kind) list.Add((pos, true));
        // Shuffle with a stream of its own (the main one must stay untouched for older layouts).
        var r = new Random(Site.Seed ^ kind.GetHashCode());
        for (int i = list.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        return list;
    }

    // ------------------------------------------------------------------ the dressing pass

    private void DressStation(Vector3 startPos)
    {
        var rng = new Random(Site.Seed ^ 0x3d55);
        var bodies = new StaticBody3D { Name = "Props", CollisionLayer = Layers.Interior, CollisionMask = 0 };
        _nav.AddChild(bodies);   // furniture is an obstacle for crawlers' paths too
        int glows = 0;
        for (int i = 0; i < _markers.Count; i++)
        {
            var (pos, kind, extras) = _markers[i];
            var basis = _markerBasis[i];
            switch (kind)
            {
                case "prop":
                {
                    if (HasTag(extras, "blockout")) break;   // the furniture geometry is already there
                    string cls = Field(extras, "class").AsString();
                    string mount = Field(extras, "mount").AsString();
                    if (string.IsNullOrEmpty(mount)) mount = "floor";
                    var fp = Vec3(Field(extras, "footprint"), new Vector3(0.6f, 0.6f, 0.6f));
                    int variant = Field(extras, "variant").VariantType == Variant.Type.Nil ? 0 : Field(extras, "variant").AsInt32();
                    string text = "";
                    var pr = Field(extras, "props");
                    if (pr.VariantType == Variant.Type.Dictionary && pr.AsGodotDictionary().TryGetValue("text", out var tx)) text = tx.AsString();
                    bool broken = HasTag(extras, "broken"), flat = HasTag(extras, "flat");
                    var node = PropFactory.Make(cls, mount, fp, variant, broken, text, rng);
                    node.Transform = new Transform3D(basis, pos);
                    AddChild(node);
                    // Live screens and tanks spill a little light, so they read as machines in a dark room.
                    if (!broken && glows < 14 && cls is "console" or "server_rack" or "specimen_tank" or "grow_rack" or "reactor_core")
                    {
                        glows++;
                        var tint = cls switch { "specimen_tank" => new Color(0.3f, 0.8f, 0.75f), "grow_rack" => new Color(0.4f, 0.9f, 0.4f), "reactor_core" => new Color(0.5f, 0.7f, 1f), _ => new Color(0.35f, 1f, 0.55f) };
                        _glowSpots.Add((pos + basis * new Vector3(0, fp.Y * 0.8f, fp.Z * 0.5f + 0.3f), tint));
                    }
                    if (PropFactory.Solid(cls, mount, fp, flat))
                        bodies.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = fp }, Transform = new Transform3D(basis, pos + Vector3.Up * fp.Y / 2f) });
                    break;
                }
                case "bulkhead":
                {
                    float w = Field(extras, "width").VariantType == Variant.Type.Nil ? 2f : Field(extras, "width").AsSingle();
                    float h = Field(extras, "height").VariantType == Variant.Type.Nil ? 2.4f : Field(extras, "height").AsSingle();
                    var door = new SlideDoor(w, h, Powered) { Transform = new Transform3D(basis, pos) };
                    door.Sound = Sound;
                    AddChild(door);
                    _bulkheads.Add(door);
                    break;
                }
                case "viewport":
                {
                    var fp = Vec3(Field(extras, "footprint"), new Vector3(0.6f, 0.1f, 0.6f));
                    AddChild(Porthole(fp, new Transform3D(basis, pos)));
                    break;
                }
                case "hazard_zone":
                {
                    var size = Vec3(Field(extras, "size"), new Vector3(3f, 1f, 3f));
                    size = new Vector3(size.X, size.Z, size.Y);   // size is [x, y up, z]: undo Vec3's [w, d, h] swap
                    bool flooded = Field(extras, "type").AsString() != "electrified";
                    var box = new Aabb(pos - new Vector3(size.X / 2f, 0f, size.Z / 2f), size);
                    _hazards.Add((box, flooded));
                    if (flooded)
                        AddChild(new MeshInstance3D
                        {
                            Mesh = new PlaneMesh { Size = new Vector2(size.X, size.Z), SubdivideWidth = 8, SubdivideDepth = 8 },
                            Position = pos + Vector3.Up * Mathf.Min(size.Y, 0.9f), MaterialOverride = WaterMaterial(),
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        });
                    else if (Powered)
                        AddChild(new OmniLight3D { Position = pos + Vector3.Up * 0.4f, LightColor = new Color(0.5f, 0.7f, 1f), LightEnergy = 0.6f, OmniRange = 4f });
                    break;
                }
                case "hiding_spot":
                    AddHidingSpot(pos, extras);
                    break;
                case "power_junction":
                    AddJunction(new Transform3D(basis, pos), Vec3(Field(extras, "footprint"), new Vector3(0.6f, 0.25f, 0.8f)));
                    break;
                case "airlock":
                {
                    var mb = new MeshBuilder();
                    mb.Box(new Vector3(0, 1.1f, 0.08f), new Vector3(1.3f, 2.3f, 0.12f), new Color(0.3f, 0.32f, 0.3f));
                    mb.Box(new Vector3(0, 1.1f, 0.16f), new Vector3(1.0f, 2.0f, 0.06f), new Color(0.75f, 0.6f, 0.15f));
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * Mathf.Tau / 8f;
                        mb.BoxOriented(new Transform3D(new Basis(Vector3.Back, a), new Vector3(Mathf.Cos(a) * 0.22f, 1.2f + Mathf.Sin(a) * 0.22f, 0.24f)), new Vector3(0.04f, 0.18f, 0.04f), new Color(0.25f, 0.25f, 0.25f));
                    }
                    mb.Box(new Vector3(0, 2.05f, 0.2f), new Vector3(0.12f, 0.08f, 0.04f), new Color(1f, 0.6f, 0.15f, 0.5f));
                    AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine), Transform = new Transform3D(basis, pos) });
                    break;
                }
            }
        }
        AddGlows();
    }

    private static Node3D Porthole(Vector3 fp, Transform3D at)
    {
        var root = new Node3D { Transform = at };
        float r = Mathf.Min(fp.X, fp.Y) * 0.5f;
        var mb = new MeshBuilder();
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.Tau / 12f;
            mb.BoxOriented(new Transform3D(new Basis(Vector3.Back, a), new Vector3(Mathf.Cos(a) * r, r + Mathf.Sin(a) * r, 0.05f)), new Vector3(0.09f, r * 0.6f, 0.1f), new Color(0.4f, 0.38f, 0.32f));
        }
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            mb.Box(new Vector3(Mathf.Cos(a) * r * 1.12f, r + Mathf.Sin(a) * r * 1.12f, 0.1f), new Vector3(0.04f, 0.04f, 0.04f), new Color(0.6f, 0.55f, 0.45f));
        }
        root.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        root.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(r * 1.9f, r * 1.9f) }, Position = new Vector3(0, r, 0.02f),
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/viewport_sea.gdshader") },
        });
        return root;
    }

    /// <summary>Dev: a station-local spot 2.4 m in front of the first marker of this kind (and prop class), facing it.</summary>
    public Transform3D? ViewOf(string kind, string cls, bool brokenOnly = false)
    {
        for (int i = 0; i < _markers.Count; i++)
        {
            var (pos, k, extras) = _markers[i];
            if (k != kind || (cls != null && Field(extras, "class").AsString() != cls) || HasTag(extras, "blockout")) continue;
            if (brokenOnly && !HasTag(extras, "broken")) continue;
            Vector3 fwd = _markerBasis[i].Z; fwd.Y = 0; fwd = fwd.LengthSquared() > 0.01f ? fwd.Normalized() : Vector3.Back;
            Vector3 eye = pos + fwd * (kind == "bulkhead" ? 3.6f : 2.4f);   // doors open within 2.6 m: look from outside that
            if (Floor(eye + Vector3.Up * 1.5f, 4f) is Vector3 f) eye = f;
            return new Transform3D(Basis.LookingAt(-fwd, Vector3.Up), eye);
        }
        return null;
    }

    // ------------------------------------------------------------------ per frame

    private void UpdateDressing(float dt)
    {
        if (Player == null || !Player.InStation) return;
        UpdateHiding(dt);
        Vector3 me = ToLocal(Player.GlobalPosition);
        foreach (var d in _bulkheads)
        {
            bool near = d.Position.DistanceTo(me) < 2.6f;
            if (!near)
                foreach (var c in Crawlers) if (c.Current != Solitude.Creatures.Crawler.State.Dead && d.Position.DistanceTo(ToLocal(c.GlobalPosition)) < 2.2f) { near = true; break; }
            d.Want = near;
        }
        bool wading = false, zapped = false;
        foreach (var (box, flooded) in _hazards)
            if (box.Grow(0.1f).HasPoint(me + Vector3.Up * 0.2f)) { if (flooded) wading = true; else if (Powered) zapped = true; }
        Player.StationWade = wading ? 0.55f : 1f;
        _zapT -= dt;
        if (zapped && _zapT <= 0f)
        {
            _zapT = 0.8f;
            Player.Damage(6f);
            Sound?.Invoke("shock", Player.GlobalPosition);
        }
    }
}

/// <summary>A pressure bulkhead: two leaves slide apart when someone comes near. Facing +Z (into the pod).</summary>
public partial class SlideDoor : Node3D
{
    public bool Want;
    public Action<string, Vector3> Sound;
    private readonly float _w, _h;
    private bool _powered;
    private MeshInstance3D _lampRed, _lampGreen;
    private Node3D _left, _right;
    private CollisionShape3D _lc, _rc;
    private float _open;
    private bool _wasOpening;

    public SlideDoor(float width, float height, bool powered) { _w = width; _h = height; _powered = powered; }
    public SlideDoor() { }

    public override void _Ready()
    {
        var frame = new MeshBuilder();
        frame.Box(new Vector3(-_w / 2f - 0.1f, _h / 2f, 0), new Vector3(0.2f, _h, 0.3f), new Color(0.3f, 0.31f, 0.3f));
        frame.Box(new Vector3(_w / 2f + 0.1f, _h / 2f, 0), new Vector3(0.2f, _h, 0.3f), new Color(0.3f, 0.31f, 0.3f));
        frame.Box(new Vector3(0, _h + 0.1f, 0), new Vector3(_w + 0.4f, 0.2f, 0.3f), new Color(0.3f, 0.31f, 0.3f));
        AddChild(new MeshInstance3D { Mesh = frame.Build(Art.Machine) });
        MeshInstance3D Lamp(Color c) { var lm = new MeshBuilder(); lm.Box(new Vector3(0, _h + 0.1f, 0.16f), new Vector3(0.2f, 0.06f, 0.02f), c); var mi = new MeshInstance3D { Mesh = lm.Build(Art.Machine) }; AddChild(mi); return mi; }
        _lampGreen = Lamp(new Color(0.3f, 1f, 0.4f, 0.5f));
        _lampRed = Lamp(new Color(1f, 0.2f, 0.1f, 0.5f));
        _lampGreen.Visible = _powered; _lampRed.Visible = !_powered;
        var body = new StaticBody3D { CollisionLayer = Layers.Interior, CollisionMask = 0 };
        AddChild(body);
        (_left, _lc) = Leaf(-1f, body);
        (_right, _rc) = Leaf(1f, body);
        if (!_powered) _open = 0.45f;   // no power: jammed where it stopped, a gap you can squeeze through
        Apply();
    }

    private (Node3D, CollisionShape3D) Leaf(float side, StaticBody3D body)
    {
        var n = new Node3D();
        // Tom's leaf (assets/props/pressure_door_leaf_<n>.glb: 1.0 x 2.4 x 0.12, origin bottom centre),
        // scaled to this door's size; the right leaf is mirrored so the two meet as a pair.
        if (PropFactory.Model("pressure_door_leaf", 0) is Node3D model)
        {
            // (turned half a circle rather than mirrored: a negative scale flips the faces and lights them black)
            model.Transform = new Transform3D(new Basis(Vector3.Up, side > 0 ? Mathf.Pi : 0f).Scaled(new Vector3((_w / 2f) / 1.0f, _h / 2.4f, 1f)), Vector3.Zero);
            n.AddChild(model);
        }
        else
        {
            var mb = new MeshBuilder();
            mb.Box(new Vector3(0, _h / 2f, 0), new Vector3(_w / 2f, _h, 0.1f), new Color(0.5f, 0.52f, 0.5f));
            mb.Box(new Vector3(-side * (_w / 4f - 0.04f), _h / 2f, 0.06f), new Vector3(0.04f, _h * 0.9f, 0.02f), new Color(0.85f, 0.65f, 0.1f));
            n.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        }
        AddChild(n);
        var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(_w / 2f, _h, 0.12f) } };
        body.AddChild(cs);
        n.SetMeta("side", side);
        return (n, cs);
    }

    private void Apply()
    {
        float off = _w / 4f + _open * (_w / 2f - 0.05f);
        _left.Position = new Vector3(-off, 0, 0);
        _right.Position = new Vector3(off, 0, 0);
        _lc.Position = new Vector3(-off, _h / 2f, 0);
        _rc.Position = new Vector3(off, _h / 2f, 0);
    }

    /// <summary>The station's power came back: the door works again (from wherever it was jammed).</summary>
    public void PowerOn()
    {
        _powered = true;
        if (_lampGreen != null) { _lampGreen.Visible = true; _lampRed.Visible = false; }
    }

    public override void _Process(double delta)
    {
        if (!_powered) return;
        float target = Want ? 1f : 0f;
        if (Want && !_wasOpening && _open < 0.5f) Sound?.Invoke("door", GlobalPosition);
        _wasOpening = Want;
        _open = Mathf.MoveToward(_open, target, (float)delta * 1.4f);
        Apply();
    }
}
