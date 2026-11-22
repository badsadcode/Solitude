using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Solitude.Core;
using Solitude.Systems;

namespace Solitude.World;

/// <summary>
/// Streams seabed chunks around a focus point (the submarine, later also the diver).
/// Heavy work (heights, mesh arrays, scatter) runs on the thread pool; node creation is
/// rate-limited on the main thread so streaming never hitches badly.
/// </summary>
public partial class TerrainStreamer : Node3D
{
    public TerrainGenerator Gen;
    public Func<Vector3> Focus;
    public int Radius = 3;
    public int MaxInFlight = 6;
    public int MaxBuildsPerFrame = 2;

    private readonly Dictionary<Vector2I, Node3D> _chunks = new();
    private readonly HashSet<Vector2I> _pending = new();
    private readonly ConcurrentQueue<ChunkData> _done = new();

    private static ParticleProcessMaterial _bubbleMat;
    private static QuadMesh _bubbleMesh;

    public int LoadedCount => _chunks.Count;

    /// <summary>Synchronously build everything around a point (used at spawn).</summary>
    public void GenerateImmediate(Vector3 focus, int radius)
    {
        var c = ChunkData.CoordOf(focus);
        for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
            {
                var k = new Vector2I(c.X + x, c.Y + z);
                if (!_chunks.ContainsKey(k)) Instantiate(ChunkData.Generate(Gen, k));
            }
    }

    public override void _Process(double delta)
    {
        if (Gen == null || Focus == null) return;
        Vector3 f = Focus();
        var centre = ChunkData.CoordOf(f);

        // Request missing chunks, nearest first.
        var wanted = new List<Vector2I>();
        for (int z = -Radius; z <= Radius; z++)
            for (int x = -Radius; x <= Radius; x++)
            {
                if (x * x + z * z > Radius * Radius + 1) continue;
                var k = new Vector2I(centre.X + x, centre.Y + z);
                if (!_chunks.ContainsKey(k) && !_pending.Contains(k)) wanted.Add(k);
            }
        wanted.Sort((a, b) => (a - centre).LengthSquared().CompareTo((b - centre).LengthSquared()));
        foreach (var k in wanted)
        {
            if (_pending.Count >= MaxInFlight) break;
            _pending.Add(k);
            var gen = Gen;
            Task.Run(() =>
            {
                try { _done.Enqueue(ChunkData.Generate(gen, k)); }
                catch (Exception e) { GD.PushError($"Chunk {k} failed: {e}"); }
            });
        }

        // Integrate finished chunks.
        int built = 0;
        while (built < MaxBuildsPerFrame && _done.TryDequeue(out var data))
        {
            _pending.Remove(data.Coord);
            if (_chunks.ContainsKey(data.Coord)) continue;
            if ((data.Coord - centre).LengthSquared() > (Radius + 2) * (Radius + 2)) continue;
            Instantiate(data);
            built++;
        }

        // Unload far chunks.
        List<Vector2I> drop = null;
        foreach (var kv in _chunks)
        {
            if ((kv.Key - centre).LengthSquared() > (Radius + 1) * (Radius + 1) + 1)
                (drop ??= new()).Add(kv.Key);
        }
        if (drop != null)
            foreach (var k in drop)
            {
                _chunks[k].QueueFree();
                _chunks.Remove(k);
                _pickups.Remove(k);
                _vents.Remove(k);
            }
    }

    private void Instantiate(ChunkData d)
    {
        var root = new Node3D { Name = $"Chunk_{d.Coord.X}_{d.Coord.Y}" };
        AddChild(root);
        _chunks[d.Coord] = root;

        var mesh = new ArrayMesh();
        MeshBuilder.AddSurface(mesh, d.Terrain, Art.Terrain);
        root.AddChild(new MeshInstance3D { Mesh = mesh });

        var body = new StaticBody3D { CollisionLayer = Layers.World, CollisionMask = 0 };
        root.AddChild(body);
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.Data = d.Collision;
        body.AddChild(new CollisionShape3D { Shape = shape });

        foreach (var (pos, radius, height) in d.SpireColliders)
        {
            body.AddChild(new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = radius, Height = height },
                Position = pos + Vector3.Up * height * 0.5f,
            });
        }
        foreach (var (pos, radius) in d.RockColliders)
        {
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = radius }, Position = pos });
        }

        for (int i = 0; i < 3; i++) AddMulti(root, Props.Rocks[i], d.Rocks[i], 0f);
        AddMulti(root, Props.Spire, d.Spires, 0f);
        AddMulti(root, Props.Stalk, d.Stalks, 110f);
        AddMulti(root, Props.Fan, d.Fans, 90f);
        AddMulti(root, Props.Chimney, d.Chimneys, 0f);
        AddMulti(root, Props.TubeWorms, d.Worms, 80f);
        AddMulti(root, Props.WhaleFall, d.WhaleFalls, 0f);

        RegisterPickups(d, root);
        _vents[d.Coord] = d.Vents;

        foreach (var v in d.Vents)
        {
            root.AddChild(new OmniLight3D
            {
                Position = v,
                LightColor = new Color(1f, 0.45f, 0.15f),
                LightEnergy = 1.6f,
                OmniRange = 22f,
                OmniAttenuation = 1.5f,
                LightVolumetricFogEnergy = 2f,
                ShadowEnabled = false,
            });
            root.AddChild(MakeVentPlume(v));
        }
    }

    // ------------------------------------------------------------------ pickups & vents

    /// <summary>A collectible lying on the seabed. Id is stable per (chunk, index).</summary>
    public sealed class Pickup
    {
        public long Id;
        public ItemId Type;
        public Vector3 Position;
        public MultiMesh Mesh;
        public int Instance;
    }

    private readonly Dictionary<Vector2I, List<Pickup>> _pickups = new();
    private readonly Dictionary<Vector2I, List<Vector3>> _vents = new();
    private readonly HashSet<long> _taken = new();

    private static long PickupId(Vector2I c, int i) => ((long)c.X << 40) ^ ((long)(c.Y & 0xFFFFF) << 16) ^ (uint)i;

    private void RegisterPickups(ChunkData d, Node3D root)
    {
        var list = new List<Pickup>();
        _pickups[d.Coord] = list;
        var byType = new Dictionary<ItemId, List<int>>();
        for (int i = 0; i < d.Collectibles.Count; i++)
        {
            var t = d.Collectibles[i].type;
            if (!byType.TryGetValue(t, out var l)) byType[t] = l = new List<int>();
            l.Add(i);
        }
        foreach (var (type, idxs) in byType)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = Props.Pickups[type] };
            mm.InstanceCount = idxs.Count;
            for (int k = 0; k < idxs.Count; k++)
            {
                int i = idxs[k];
                long id = PickupId(d.Coord, i);
                var xf = d.Collectibles[i].xform;
                bool taken = _taken.Contains(id);
                mm.SetInstanceTransform(k, taken ? new Transform3D(Basis.FromScale(Vector3.Zero), xf.Origin) : xf);
                if (!taken) list.Add(new Pickup { Id = id, Type = type, Position = xf.Origin + xf.Basis.Y * 0.3f, Mesh = mm, Instance = k });
            }
            root.AddChild(new MultiMeshInstance3D { Multimesh = mm });
        }
    }

    /// <summary>Nearest untaken pickup within maxDist of p, or null.</summary>
    public Pickup NearestPickup(Vector3 p, float maxDist)
    {
        Pickup best = null;
        float bestD = maxDist;
        var c = ChunkData.CoordOf(p);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!_pickups.TryGetValue(new Vector2I(c.X + dx, c.Y + dz), out var list)) continue;
                foreach (var pk in list)
                {
                    float d = pk.Position.DistanceTo(p);
                    if (d < bestD) { bestD = d; best = pk; }
                }
            }
        return best;
    }

    /// <summary>Remove a pickup from the world for good.</summary>
    public void Take(Pickup pk)
    {
        _taken.Add(pk.Id);
        pk.Mesh.SetInstanceTransform(pk.Instance, new Transform3D(Basis.FromScale(Vector3.Zero), pk.Position));
        foreach (var list in _pickups.Values) list.Remove(pk);
    }

    public float NearestVentDistance(Vector3 p)
    {
        float best = float.MaxValue;
        var c = ChunkData.CoordOf(p);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (_vents.TryGetValue(new Vector2I(c.X + dx, c.Y + dz), out var vs))
                    foreach (var v in vs) best = Mathf.Min(best, v.DistanceTo(p));
        return best;
    }

    private static void AddMulti(Node3D parent, Mesh mesh, List<Transform3D> xforms, float visEnd)
    {
        if (xforms.Count == 0) return;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
        mm.InstanceCount = xforms.Count;
        for (int i = 0; i < xforms.Count; i++) mm.SetInstanceTransform(i, xforms[i]);
        var inst = new MultiMeshInstance3D { Multimesh = mm };
        if (visEnd > 0f) inst.VisibilityRangeEnd = visEnd;
        parent.AddChild(inst);
    }

    /// <summary>Shimmering column of mineral-laden water and bubbles rising from a vent.</summary>
    private static GpuParticles3D MakeVentPlume(Vector3 pos)
    {
        if (_bubbleMat == null)
        {
            var pm = new ParticleProcessMaterial
            {
                Direction = Vector3.Up,
                Spread = 12f,
                InitialVelocityMin = 1.2f,
                InitialVelocityMax = 2.5f,
                Gravity = new Vector3(0, 0.6f, 0),
                ScaleMin = 0.4f,
                ScaleMax = 1.4f,
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = 0.6f,
                TurbulenceEnabled = true,
                TurbulenceNoiseStrength = 0.6f,
                TurbulenceNoiseScale = 2f,
            };
            _bubbleMat = pm;
            _bubbleMesh = new QuadMesh
            {
                Size = new Vector2(0.12f, 0.12f),
                Material = new StandardMaterial3D
                {
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(0.9f, 0.7f, 0.5f, 0.55f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                },
            };
        }
        return new GpuParticles3D
        {
            Position = pos - Vector3.Up * 1.5f,
            Amount = 40,
            Lifetime = 7f,
            Preprocess = 7f,
            ProcessMaterial = _bubbleMat,
            DrawPass1 = _bubbleMesh,
            VisibilityAabb = new Aabb(new Vector3(-6, -2, -6), new Vector3(12, 30, 12)),
        };
    }
}
