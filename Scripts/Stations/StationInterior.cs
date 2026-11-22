using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Creatures;
using Solitude.Interior;
using Solitude.Player;
using Solitude.Systems;

namespace Solitude.Stations;

/// <summary>What the player changed aboard a station; survives undocking and redocking.</summary>
public sealed class StationState
{
    public readonly HashSet<int> Taken = new();
    public readonly HashSet<int> Killed = new();
    public bool DoorOpen, SecretOpen, HasKeycard, Visited;
}

/// <summary>
/// Builds a walkable station from a NoorLevel blockout (assets/stations/*.glb) and fills it.
///
/// Import rules (Noor Interchange first, NoorLevel's current export second):
///   - Markers: extras.noor.marker if present, else NoorLevel's extras.kind
///     (player_start, light, exit, key, door, secret_door).
///   - Surfaces by material: nl_floor / nl_wall / nl_ceiling are restyled; nl_door and nl_secret
///     are cut out into their own openable bodies; nl_sky and nl_trigger are never rendered.
/// Contents (Solitude decides, NoorLevel only makes the space): power state and lights, loot,
/// crew logs, a keycard, the core with a map fragment, and possibly an infestation.
/// </summary>
public partial class StationInterior : Node3D
{
    public StationSite Site;
    public StationState State;
    public PlayerController Player;
    public ShipSystems Systems;
    public Weapons Weapons;

    public Action<string> Message;
    public Action<StationLogs.Log> ReadLog;
    public Action<StationSite> MapFragment;
    public Action ReturnToSub;
    public Action<string, Vector3> Sound;          // creature/station sounds at a position

    public Transform3D AirlockPose { get; private set; }
    public bool Powered { get; private set; }
    public readonly List<Crawler> Crawlers = new();
    public bool Ready { get; private set; }

    private NavigationRegion3D _nav;
    private readonly List<(Vector3 pos, string kind, Godot.Collections.Dictionary extras)> _markers = new();
    private readonly List<(Vector3 a, Vector3 b, Vector3 c, bool secret)> _openables = new();
    private readonly List<(OmniLight3D l, float e, bool flicker)> _lights = new();
    private Random _rng;
    private int _placeFrame = -1;
    private float _t;

    public override void _Ready()
    {
        _rng = new Random(Site.Seed);
        Powered = _rng.NextDouble() < 0.5 || Site.Id == -1 && _rng.NextDouble() < 0.3;
        _nav = new NavigationRegion3D
        {
            NavigationMesh = new NavigationMesh
            {
                GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
                GeometryCollisionMask = Layers.Interior,
                CellSize = 0.25f, CellHeight = 0.25f,
                AgentRadius = 0.5f, AgentHeight = 1.0f, AgentMaxClimb = 0.5f, AgentMaxSlope = 50f, // multiples of the 0.25 m voxel
            },
        };
        AddChild(_nav);
        LoadBlockout();
        BuildOpenables();
        BuildLights();
        _placeFrame = 2; // wait for the physics space to see the new colliders before raycasting
    }

    // ------------------------------------------------------------------ geometry

    private void LoadBlockout()
    {
        var scene = GD.Load<PackedScene>($"res://assets/stations/{Site.Layout}.glb");
        var root = scene.Instantiate<Node3D>();
        AddChild(root);
        var meshes = new List<MeshInstance3D>();
        Walk(root, meshes);

        var body = new StaticBody3D { CollisionLayer = Layers.Interior, CollisionMask = 0, Name = "Hull" };
        _nav.AddChild(body);
        foreach (var mi in meshes)
        {
            var src = mi.Mesh as ArrayMesh;
            if (src == null) continue;
            Transform3D toLocal = GlobalTransform.AffineInverse() * mi.GlobalTransform;
            var keep = new ArrayMesh();
            for (int s = 0; s < src.GetSurfaceCount(); s++)
            {
                string mat = (src.SurfaceGetMaterial(s)?.ResourceName ?? src.SurfaceGetName(s) ?? "").ToLowerInvariant();
                var arrays = src.SurfaceGetArrays(s);
                if (mat.Contains("sky") || mat.Contains("trigger")) continue;
                if (mat.Contains("door") || mat.Contains("secret"))
                {
                    CollectTriangles(arrays, toLocal, mat.Contains("secret"));
                    continue;
                }
                keep.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                Material m = mat.Contains("floor") ? Art.StationFloor : mat.Contains("ceiling") ? Art.StationCeiling : Art.StationWall;
                keep.SurfaceSetMaterial(keep.GetSurfaceCount() - 1, m);
            }
            if (keep.GetSurfaceCount() == 0) continue;
            var inst = new MeshInstance3D { Mesh = keep, Transform = toLocal };
            AddChild(inst);
            var shape = keep.CreateTrimeshShape();
            if (shape != null) body.AddChild(new CollisionShape3D { Shape = shape, Transform = toLocal });
        }
        root.QueueFree();
    }

    private void Walk(Node n, List<MeshInstance3D> meshes)
    {
        if (n is MeshInstance3D mi) meshes.Add(mi);
        else if (n is Node3D n3 && n.HasMeta("extras"))
        {
            var extras = n.GetMeta("extras").AsGodotDictionary();
            string kind = null;
            if (extras.TryGetValue("noor", out var noorV) && noorV.VariantType == Variant.Type.Dictionary
                && noorV.AsGodotDictionary().TryGetValue("marker", out var mk)) kind = mk.AsString();   // Noor Interchange marker
            else if (extras.TryGetValue("kind", out var k)) kind = k.AsString();                     // NoorLevel today
            if (kind != null) _markers.Add((ToLocal(n3.GlobalPosition), Normalise(kind), extras));
        }
        foreach (var c in n.GetChildren()) Walk(c, meshes);
    }

    private static string Normalise(string kind) => kind switch
    {
        "spawn.player" => "player_start",
        "entrance" => "player_start",
        _ => kind,
    };

    private void CollectTriangles(Godot.Collections.Array arrays, Transform3D xf, bool secret)
    {
        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var idxV = arrays[(int)Mesh.ArrayType.Index];
        int[] idx = idxV.VariantType == Variant.Type.Nil ? null : idxV.AsInt32Array();
        int n = idx?.Length ?? v.Length;
        for (int i = 0; i + 2 < n; i += 3)
        {
            Vector3 a = v[idx?[i] ?? i], b = v[idx?[i + 1] ?? i + 1], c = v[idx?[i + 2] ?? i + 2];
            _openables.Add((xf * a, xf * b, xf * c, secret));
        }
    }

    /// <summary>Cut-out door / secret surfaces become openable slabs, grouped by their marker.</summary>
    private void BuildOpenables()
    {
        var doors = _markers.FindAll(m => m.kind == "door" || m.kind == "secret_door");
        if (doors.Count == 0 || _openables.Count == 0) return;
        var groups = new Dictionary<int, MeshBuilder>();
        var faces = new Dictionary<int, List<Vector3>>();
        foreach (var (a, b, c, secret) in _openables)
        {
            Vector3 centre = (a + b + c) / 3f;
            int best = -1;
            float bestD = 6f;
            for (int i = 0; i < doors.Count; i++)
            {
                if ((doors[i].kind == "secret_door") != secret) continue;
                float d = doors[i].pos.DistanceTo(centre);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            if (!groups.TryGetValue(best, out var mb)) { groups[best] = mb = new MeshBuilder(); faces[best] = new List<Vector3>(); }
            mb.TriWorld(a, b, c, Colors.White);
            faces[best].Add(a); faces[best].Add(c); faces[best].Add(b);
        }
        foreach (var (i, mb) in groups)
        {
            var (pos, kind, extras) = doors[i];
            bool secret = kind == "secret_door";
            bool open = secret ? State.SecretOpen : State.DoorOpen;
            string keyColour = extras.TryGetValue("key", out var kc) ? kc.AsString() : "";
            var door = new Interactable { Prompt = secret ? "Hollow-sounding panel" : $"Bulkhead door ({keyColour} keycard)", CollisionLayer = Layers.Interior | Layers.Use };
            var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
            shape.Data = faces[i].ToArray();
            door.AddChild(new CollisionShape3D { Shape = shape });
            door.AddChild(new MeshInstance3D { Mesh = mb.Build(secret ? Art.StationWall : Art.StationDoor) });
            _nav.AddChild(door);
            if (open) { door.Position = Vector3.Up * 3.2f; door.CollisionLayer = 0; door.Visible = false; continue; }
            door.OnUse = () => TryOpen(door, secret, keyColour);
        }
    }

    private void TryOpen(Interactable door, bool secret, string key)
    {
        if (!secret && !State.HasKeycard) { Message?.Invoke($"LOCKED. The reader blinks red: {key.ToUpperInvariant()} KEYCARD REQUIRED."); Sound?.Invoke("deny", door.GlobalPosition); return; }
        if (secret) State.SecretOpen = true; else State.DoorOpen = true;
        Sound?.Invoke("door", door.GlobalPosition);
        Message?.Invoke(secret ? "The panel gives. There's a space behind it." : "The keycard reader turns green. The bulkhead grinds upward.");
        door.OnUse = null;
        door.Prompt = "";
        var tw = CreateTween();
        tw.TweenProperty(door, "position", door.Position + Vector3.Up * 3.2f, 2.5).SetTrans(Tween.TransitionType.Sine);
        tw.Finished += () => { door.CollisionLayer = 0; door.Visible = false; _nav.BakeNavigationMesh(true); };
    }

    // ------------------------------------------------------------------ lights

    private void BuildLights()
    {
        var fixture = new MeshBuilder();
        fixture.Box(Vector3.Zero, new Vector3(0.5f, 0.08f, 0.25f), new Color(0.2f, 0.2f, 0.2f));
        var litMb = new MeshBuilder();
        litMb.Box(new Vector3(0, -0.05f, 0), new Vector3(0.42f, 0.03f, 0.18f), new Color(0.9f, 0.95f, 1f, 0.5f));
        var redMb = new MeshBuilder();
        redMb.Box(new Vector3(0, -0.05f, 0), new Vector3(0.42f, 0.03f, 0.18f), new Color(1f, 0.15f, 0.1f, 0.5f));
        var fixtureMesh = fixture.Build(Art.Machine);
        var lit = litMb.Build(Art.Machine);
        var red = redMb.Build(Art.Machine);

        int n = 0;
        foreach (var (pos, kind, _) in _markers)
        {
            if (kind != "light") continue;
            n++;
            AddChild(new MeshInstance3D { Mesh = fixtureMesh, Position = pos });
            double roll = _rng.NextDouble();
            if (Powered)
            {
                if (roll < 0.18) continue; // dead tube
                AddChild(new MeshInstance3D { Mesh = lit, Position = pos });
                var l = new OmniLight3D { Position = pos - Vector3.Up * 0.3f, LightColor = new Color(0.8f, 0.9f, 1f), LightEnergy = 0.75f, OmniRange = 8f, OmniAttenuation = 1.4f, ShadowEnabled = n % 6 == 0 };
                AddChild(l);
                _lights.Add((l, 0.75f, roll < 0.33));
            }
            else if (n % 3 == 0)
            {
                AddChild(new MeshInstance3D { Mesh = red, Position = pos });
                var l = new OmniLight3D { Position = pos - Vector3.Up * 0.3f, LightColor = new Color(1f, 0.12f, 0.08f), LightEnergy = 0.45f, OmniRange = 6f, OmniAttenuation = 1.5f };
                AddChild(l);
                _lights.Add((l, 0.45f, roll < 0.2));
            }
        }
    }

    // ------------------------------------------------------------------ contents (deferred one physics frame)

    public override void _PhysicsProcess(double delta)
    {
        if (_placeFrame < 0) return;
        if (--_placeFrame > 0) return;
        _placeFrame = -1;
        PlaceContents();
        _nav.BakeNavigationMesh(true);
        Ready = true;
    }

    private Vector3? Floor(Vector3 from, float drop = 20f)
    {
        var space = GetWorld3D().DirectSpaceState;
        var g = GlobalTransform;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(g * from, g * (from - Vector3.Up * drop), Layers.Interior));
        if (hit.Count == 0) return null;
        if (((Vector3)hit["normal"]).Y < 0.7f) return null;
        return ToLocal((Vector3)hit["position"]);
    }

    private void PlaceContents()
    {
        // Airlock: where NoorLevel put the player start.
        var start = _markers.Find(m => m.kind == "player_start");
        Vector3 startPos = start.kind != null ? (Floor(start.pos + Vector3.Up * 0.5f, 6f) ?? start.pos) : Vector3.Zero;
        float yaw = 0f;
        if (start.kind != null && start.extras.TryGetValue("angle", out var ang))
            yaw = Mathf.DegToRad(ang.AsSingle() - 90f); // Quake-style angle (0 = +X) → Godot yaw (0 = -Z)
        AirlockPose = new Transform3D(new Basis(Vector3.Up, yaw), startPos);
        var hatch = new MeshBuilder();
        hatch.Cylinder(Vector3.Zero, 0.7f, 0.7f, 0.06f, 10, new Color(0.9f, 0.7f, 0.1f));
        hatch.Cylinder(new Vector3(0, 0.06f, 0), 0.5f, 0.5f, 0.05f, 10, new Color(0.25f, 0.25f, 0.25f));
        AddChild(new MeshInstance3D { Mesh = hatch.Build(Art.Machine), Position = startPos + Vector3.Up * 0.02f });
        var ret = Interactable.Create(this, startPos + Vector3.Up * 0.3f, new Vector3(1.4f, 0.6f, 1.4f), "Airlock: return to the submarine", () => ReturnToSub?.Invoke());
       AddChild(new OmniLight3D { Position = startPos + Vector3.Up * 2.2f, LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 0.9f, OmniRange = 6f });

        // Candidate floor points under the light fittings (rooms and corridors).
        var spots = new List<Vector3>();
        foreach (var (pos, kind, _) in _markers)
            if (kind == "light" && Floor(pos) is Vector3 f && f.DistanceTo(startPos) > 4f) spots.Add(f);
        Shuffle(spots);

        int lootIndex = 0;
        void Loot(Vector3 p, string label, Mesh mesh, Action take)
        {
            int id = lootIndex++;
            if (State.Taken.Contains(id)) return;
            var it = Interactable.Create(this, p + Vector3.Up * 0.25f, new Vector3(0.7f, 0.5f, 0.7f), label, null);
           var vis = new MeshInstance3D { Mesh = mesh, Position = Vector3.Down * 0.25f, Rotation = new Vector3(0, (float)_rng.NextDouble() * Mathf.Tau, 0) };
            it.AddChild(vis);
            it.OnUse = () => { State.Taken.Add(id); take(); it.QueueFree(); Sound?.Invoke("pickup", it.GlobalPosition); };
        }

        var crate = Crate(new Color(0.35f, 0.4f, 0.3f));
        var medbox = Crate(new Color(0.8f, 0.8f, 0.78f), new Color(0.9f, 0.1f, 0.1f, 0.5f));
        var ammobox = Crate(new Color(0.3f, 0.32f, 0.2f), new Color(1f, 0.8f, 0.2f, 0.5f));
        var datapad = Datapad();
        var gunCase = GunCase();
        var keycard = Keycard();

        // Exit marker → the station core: map fragment, final log, a power cell.
        var exit = _markers.Find(m => m.kind == "exit");
        int logCount = 3 + _rng.Next(3);
        var logs = StationLogs.Build(Site.Name, Site.Seed, logCount);
        if (exit.kind != null && Floor(exit.pos + Vector3.Up) is Vector3 core)
        {
            BuildCoreTerminal(core);
            Loot(core + new Vector3(0.9f, 0, 0), "Map fragment (core memory)", datapad, () => MapFragment?.Invoke(Site));
            Loot(core + new Vector3(-0.9f, 0, 0), "Read crew log", datapad, () => ReadLog?.Invoke(logs[^1]));
            Loot(core + new Vector3(0, 0, 0.9f), "Power cell", crate, () => Systems.Collect(ItemId.PowerCell));
        }

        // Keycard where NoorLevel put the key.
        var key = _markers.Find(m => m.kind == "key");
        if (key.kind != null && !State.DoorOpen && Floor(key.pos + Vector3.Up) is Vector3 kp)
            Loot(kp, "Keycard (red)", keycard, () => { State.HasKeycard = true; Message?.Invoke("RED KEYCARD. Somebody's photo on it has been scratched out."); });

        // Earlier logs, then general loot, across the floor spots.
        int s = 0;
        for (int i = 0; i < logs.Count - 1 && s < spots.Count; i++, s++)
        {
            var log = logs[i];
            Loot(spots[s], "Read crew log", datapad, () => ReadLog?.Invoke(log));
        }
        // A weapon: the starter station always has the rifle; others sometimes a shotgun or rifle.
        if (s < spots.Count && (Site.Id == -1 || _rng.NextDouble() < 0.45))
        {
            WeaponId w = Site.Id == -1 ? WeaponId.Rifle : (_rng.NextDouble() < 0.5 ? WeaponId.Shotgun : WeaponId.Rifle);
            Loot(spots[s++], $"Weapon locker: {Weapons.Defs[w].Name.ToLowerInvariant()}", gunCase, () =>
            {
                Weapons.Give(w, w == WeaponId.Shotgun ? 6 : 30);
                Weapons.AddAmmo(w, w == WeaponId.Shotgun ? 6 : 30);
                Message?.Invoke($"{Weapons.Defs[w].Name} taken. Press {(w == WeaponId.Rifle ? 2 : 3)} to select.");
            });
        }
        int lootCount = 6 + _rng.Next(7);
        for (int i = 0; i < lootCount && s < spots.Count; i++, s++)
        {
            double r = _rng.NextDouble();
            var p = spots[s] + new Vector3((float)_rng.NextDouble() - 0.5f, 0, (float)_rng.NextDouble() - 0.5f);
            if (r < 0.22) Loot(p, "Station tin", crate, () => Systems.Collect(ItemId.CannedFood));
            else if (r < 0.34) Loot(p, "Medkit", medbox, () => Systems.Collect(ItemId.Medkit));
            else if (r < 0.62)
            {
                WeaponId w = (WeaponId)_rng.Next(3);
                int n = w == WeaponId.Shotgun ? 6 : w == WeaponId.Rifle ? 30 : 12;
                Loot(p, $"Ammunition ({Weapons.Defs[w].Name.ToLowerInvariant()})", ammobox, () => { Weapons.AddAmmo(w, n); Message?.Invoke($"+{n} rounds ({Weapons.Defs[w].Name.ToLowerInvariant()})."); });
            }
            else
            {
                ItemId[] mats = { ItemId.MetalPlate, ItemId.Circuit, ItemId.Crystal, ItemId.PatchKit, ItemId.PowerCell, ItemId.Sealant };
                var item = mats[_rng.Next(mats.Length)];
                Loot(p, Items.Name(item), crate, () => Systems.Collect(item));
            }
        }

        // Infestation: some stations are empty. Most are not.
        int crawlers = Site.Id == -1 ? 3 : (_rng.NextDouble() < 0.3 ? 0 : 2 + _rng.Next(5));
        for (int i = 0; i < crawlers && s < spots.Count; i++, s++)
        {
            if (spots[s].DistanceTo(startPos) < 12f) continue;
            var c = new Crawler { Player = Player, Index = i, Position = spots[s] + Vector3.Up * 0.3f, Rotation = new Vector3(0, (float)_rng.NextDouble() * Mathf.Tau, 0) };
            c.Sound = (kind, pos) => Sound?.Invoke(kind, pos);
            c.Killed = k => State.Killed.Add(k.Index);
            AddChild(c);
            if (State.Killed.Contains(i)) c.SetCorpse();
            Crawlers.Add(c);
        }

        State.Visited = true;
    }

    private void BuildCoreTerminal(Vector3 p)
    {
        var mb = new MeshBuilder();
        mb.Box(p + new Vector3(0, 0.7f, -1.1f), new Vector3(1.6f, 1.4f, 0.5f), new Color(0.3f, 0.32f, 0.33f));
        mb.Box(p + new Vector3(0, 1.1f, -0.84f), new Vector3(1.2f, 0.6f, 0.04f), new Color(0.2f, 0.8f, 0.6f, 0.4f));
        for (int i = 0; i < 6; i++)
            mb.Box(p + new Vector3(-0.6f + i * 0.24f, 0.55f, -0.84f), new Vector3(0.1f, 0.06f, 0.04f), i % 2 == 0 ? new Color(1f, 0.3f, 0.2f, 0.5f) : new Color(0.3f, 1f, 0.4f, 0.5f));
        AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        AddChild(new OmniLight3D { Position = p + new Vector3(0, 1.4f, -0.4f), LightColor = new Color(0.3f, 0.9f, 0.7f), LightEnergy = 0.8f, OmniRange = 5f });
        var lbl = new Label3D { Text = "CORE  ·  " + Site.Name, Position = p + new Vector3(0, 1.75f, -0.84f), FontSize = 32, PixelSize = 0.004f, Modulate = new Color(0.4f, 1f, 0.7f), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
        AddChild(lbl);
    }

    private static ArrayMesh Crate(Color body, Color? stripe = null)
    {
        var mb = new MeshBuilder();
        mb.Box(new Vector3(0, 0.2f, 0), new Vector3(0.6f, 0.4f, 0.45f), body);
        mb.Box(new Vector3(0, 0.41f, 0), new Vector3(0.62f, 0.03f, 0.47f), body.Shade(0.7f));
        if (stripe is Color s) mb.Box(new Vector3(0, 0.25f, 0.23f), new Vector3(0.3f, 0.12f, 0.01f), s);
        return mb.Build(Art.Machine);
    }

    private static ArrayMesh Datapad()
    {
        var mb = new MeshBuilder();
        mb.Box(new Vector3(0, 0.02f, 0), new Vector3(0.22f, 0.03f, 0.3f), new Color(0.15f, 0.15f, 0.15f));
        mb.Box(new Vector3(0, 0.04f, 0), new Vector3(0.18f, 0.01f, 0.24f), new Color(0.3f, 1f, 0.6f, 0.4f));
        return mb.Build(Art.Machine);
    }

    private static ArrayMesh GunCase()
    {
        var mb = new MeshBuilder();
        mb.Box(new Vector3(0, 0.12f, 0), new Vector3(1.1f, 0.24f, 0.4f), new Color(0.25f, 0.28f, 0.22f));
        mb.Box(new Vector3(0, 0.25f, 0), new Vector3(0.4f, 0.03f, 0.15f), new Color(1f, 0.75f, 0.2f, 0.5f));
        return mb.Build(Art.Machine);
    }

    private static ArrayMesh Keycard()
    {
        var mb = new MeshBuilder();
        mb.Box(new Vector3(0, 0.01f, 0), new Vector3(0.12f, 0.01f, 0.08f), new Color(1f, 0.15f, 0.1f, 0.4f));
        return mb.Build(Art.Machine);
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        foreach (var (l, e, flicker) in _lights)
        {
            if (flicker && _rng.NextDouble() < 0.04) l.LightEnergy = e * (float)_rng.NextDouble() * 0.3f;
            else l.LightEnergy = Mathf.Lerp(l.LightEnergy, e, 10f * dt);
        }
    }

    /// <summary>A walkable point `dist` metres along the navmesh path from → to (global space).</summary>
    public Vector3 PointAlongPath(Vector3 from, Vector3 to, float dist)
    {
        var path = NavigationServer3D.MapGetPath(GetWorld3D().NavigationMap, from, to, true);
        for (int i = 1; i < path.Length; i++)
        {
            float seg = path[i].DistanceTo(path[i - 1]);
            if (dist <= seg) return path[i - 1].Lerp(path[i], dist / seg);
            dist -= seg;
        }
        return path.Length > 0 ? path[^1] : from;
    }

    public string NavDiagnostics(Vector3 from, Vector3 to)
    {
        var nm = _nav.NavigationMesh;
        var map = GetWorld3D().NavigationMap;
        var path = NavigationServer3D.MapGetPath(map, from, to, true);
        Vector3 closest = NavigationServer3D.MapGetClosestPoint(map, from);
        float len = 0f; for (int i = 1; i < path.Length; i++) len += path[i].DistanceTo(path[i - 1]);
        string ends = path.Length > 0 ? $" first={ToLocal(path[0])} last={ToLocal(path[^1])} target={ToLocal(to)}" : "";
        return $"polys={nm.GetPolygonCount()} path={path.Length} pts, length={len:0.0} m, straight={from.DistanceTo(to):0.0} m, fromSnap={closest.DistanceTo(from):0.00} m{ends}";
    }

    /// <summary>A gunshot or a running captain: tell every crawler.</summary>
    public void Noise(Vector3 pos, float radius)
    {
        foreach (var c in Crawlers) c.Hear(pos, radius);
    }
}
