using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Systems;

namespace Solitude.World;

/// <summary>
/// Everything needed to instantiate one terrain chunk. Produced on a worker thread from
/// plain C# data; turned into nodes on the main thread by TerrainStreamer.
/// </summary>
public sealed class ChunkData
{
    public const float Size = 64f;
    public const int Res = 16; // cells per side → 4 m facets
    public const float Cell = Size / Res;

    public Vector2I Coord;
    public (Vector3[] verts, Vector3[] normals, Color[] colors, Vector2[] uvs) Terrain;
    public Vector3[] Collision;

    public readonly List<Transform3D>[] Rocks = { new(), new(), new() };
    public readonly List<Transform3D> Spires = new();
    public readonly List<Transform3D> Stalks = new();
    public readonly List<Transform3D> Fans = new();
    public readonly List<Transform3D> Chimneys = new();
    public readonly List<Transform3D> Worms = new();
    public readonly List<Transform3D> WhaleFalls = new();
    public readonly List<(Vector3 pos, float radius, float height)> SpireColliders = new();
    public readonly List<(Vector3 pos, float radius)> RockColliders = new();
    public readonly List<Vector3> Vents = new();
    public readonly List<(ItemId type, Transform3D xform)> Collectibles = new();

    public static Vector2I CoordOf(Vector3 p) => new((int)MathF.Floor(p.X / Size), (int)MathF.Floor(p.Z / Size));

    public static ChunkData Generate(TerrainGenerator gen, Vector2I coord)
    {
        var d = new ChunkData { Coord = coord };
        float ox = coord.X * Size, oz = coord.Y * Size;

        // --- height grid
        var h = new float[Res + 1, Res + 1];
        for (int z = 0; z <= Res; z++)
            for (int x = 0; x <= Res; x++)
                h[x, z] = gen.Height(ox + x * Cell, oz + z * Cell);

        // --- faceted surface, colour per triangle
        var mb = new MeshBuilder { UvScale = 1f };
        for (int z = 0; z < Res; z++)
        {
            for (int x = 0; x < Res; x++)
            {
                Vector3 p00 = new(ox + x * Cell, h[x, z], oz + z * Cell);
                Vector3 p10 = new(ox + (x + 1) * Cell, h[x + 1, z], oz + z * Cell);
                Vector3 p01 = new(ox + x * Cell, h[x, z + 1], oz + (z + 1) * Cell);
                Vector3 p11 = new(ox + (x + 1) * Cell, h[x + 1, z + 1], oz + (z + 1) * Cell);
                // Alternate the diagonal for a less regular look.
                bool flip = ((x + z) & 1) == 0;
                if (flip)
                {
                    AddGroundTri(mb, gen, p00, p01, p10);
                    AddGroundTri(mb, gen, p10, p01, p11);
                }
                else
                {
                    AddGroundTri(mb, gen, p00, p01, p11);
                    AddGroundTri(mb, gen, p00, p11, p10);
                }
            }
        }
        d.Terrain = mb.ToArrays();
        d.Collision = d.Terrain.verts;

        Scatter(d, gen, ox, oz);
        return d;
    }

    private static void AddGroundTri(MeshBuilder mb, TerrainGenerator gen, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = (b - a).Cross(c - a).Normalized();
        if (n.Y < 0) n = -n;
        Color col = gen.GroundColor((a + b + c) / 3f, n);
        mb.TriWorld(a, b, c, col, Vector3.Up);
    }

    private static Transform3D Place(Vector3 pos, float yaw, Vector3 scale, Vector3 up, float tiltFollow)
    {
        var basis = new Basis(Vector3.Up, yaw);
        if (tiltFollow > 0f)
        {
            Vector3 target = Vector3.Up.Lerp(up, tiltFollow).Normalized();
            Vector3 axis = Vector3.Up.Cross(target);
            if (axis.LengthSquared() > 1e-6f)
                basis = new Basis(axis.Normalized(), Vector3.Up.AngleTo(target)) * basis;
        }
        return new Transform3D(basis * Basis.FromScale(scale), pos);
    }

    private static void Scatter(ChunkData d, TerrainGenerator gen, float ox, float oz)
    {
        var rng = new Random((int)(Perlin.Hash(d.Coord.X, d.Coord.Y, gen.Seed) * int.MaxValue));
        float R() => (float)rng.NextDouble();

        // Rocks and boulders
        int rocks = 5 + (int)(R() * 14);
        for (int i = 0; i < rocks; i++)
        {
            float x = ox + R() * Size, z = oz + R() * Size;
            float y = gen.Height(x, z);
            float s = 0.5f + R() * R() * 3.5f;
            if (R() < 0.06f) s *= 2.2f;
            var sc = new Vector3(s * (0.8f + R() * 0.5f), s * (0.6f + R() * 0.5f), s * (0.8f + R() * 0.5f));
            var t = Place(new Vector3(x, y - s * 0.25f, z), R() * Mathf.Tau, sc, gen.Normal(x, z), 0.6f);
            d.Rocks[i % 3].Add(t);
            if (s > 2.6f) d.RockColliders.Add((new Vector3(x, y, z), s * 0.85f));
        }

        // Spires — tall rock needles
        float spireChance = 0.14f;
        if (R() < spireChance)
        {
            int n = 1 + (int)(R() * 3);
            for (int i = 0; i < n; i++)
            {
                float x = ox + 8 + R() * (Size - 16), z = oz + 8 + R() * (Size - 16);
                float y = gen.Height(x, z);
                float r = 2f + R() * 3.5f, hgt = 14f + R() * R() * 45f;
                d.Spires.Add(Place(new Vector3(x, y - 2f, z), R() * Mathf.Tau, new Vector3(r, hgt, r), Vector3.Up, 0f));
                d.SpireColliders.Add((new Vector3(x, y - 2f, z), r * 0.8f, hgt));
            }
        }

        // Bioluminescent meadows
        float centreDensity = gen.FloraDensity(ox + Size * 0.5f, oz + Size * 0.5f);
        int stalks = (int)(centreDensity * 70);
        for (int i = 0; i < stalks; i++)
        {
            float x = ox + R() * Size, z = oz + R() * Size;
            float dens = gen.FloraDensity(x, z);
            if (R() > dens) continue;
            Vector3 nrm = gen.Normal(x, z);
            if (nrm.Y < 0.75f) continue;
            float s = 1.2f + R() * 3.5f;
            d.Stalks.Add(Place(new Vector3(x, gen.Height(x, z) - 0.1f, z), R() * Mathf.Tau, new Vector3(s, s, s), nrm, 0.3f));
        }

        int fans = (int)(centreDensity * 18) + (R() < 0.3f ? 3 : 0);
        for (int i = 0; i < fans; i++)
        {
            float x = ox + R() * Size, z = oz + R() * Size;
            Vector3 nrm = gen.Normal(x, z);
            float s = 0.8f + R() * 2.2f;
            d.Fans.Add(Place(new Vector3(x, gen.Height(x, z) - 0.05f, z), R() * Mathf.Tau, new Vector3(s, s, s), nrm, 0.7f));
        }

        // Hydrothermal vents
        float vent = gen.VentField(ox + Size * 0.5f, oz + Size * 0.5f);
        if (vent > 0.4f && R() < 0.65f)
        {
            int n = 1 + (int)(R() * 3);
            for (int i = 0; i < n; i++)
            {
                float x = ox + 6 + R() * (Size - 12), z = oz + 6 + R() * (Size - 12);
                float y = gen.Height(x, z);
                float s = 2f + R() * 4f;
                d.Chimneys.Add(Place(new Vector3(x, y - 0.3f, z), R() * Mathf.Tau, new Vector3(s, s * (1.2f + R() * 1.5f), s), Vector3.Up, 0f));
                d.Vents.Add(new Vector3(x, y + s * 2f, z));
                for (int w = 0; w < 4; w++)
                {
                    float wx = x + (R() - 0.5f) * 10f, wz = z + (R() - 0.5f) * 10f;
                    float ws = 1f + R() * 1.5f;
                    d.Worms.Add(Place(new Vector3(wx, gen.Height(wx, wz) - 0.05f, wz), R() * Mathf.Tau, new Vector3(ws, ws, ws), gen.Normal(wx, wz), 0.8f));
                }
            }
        }

        // Rare: the bones of something enormous.
        if (Perlin.Hash(d.Coord.X, d.Coord.Y, gen.Seed + 777) < 0.02f)
        {
            float x = ox + Size * 0.5f, z = oz + Size * 0.5f;
            Vector3 nrm = gen.Normal(x, z, 6f);
            if (nrm.Y > 0.85f)
            {
                float s = 1f + R() * 0.6f;
                d.WhaleFalls.Add(Place(new Vector3(x, gen.Height(x, z) - 0.8f, z), R() * Mathf.Tau, new Vector3(s, s, s), nrm, 1f));
            }
        }

        ScatterCollectibles(d, gen, ox, oz, rng);
    }

    /// <summary>
    /// Things the arm can pick up. Separate RNG stream position is fine: everything here is
    /// deterministic per (seed, chunk), and collectible index → stable id for "already taken".
    /// </summary>
    private static void ScatterCollectibles(ChunkData d, TerrainGenerator gen, float ox, float oz, Random rng)
    {
        float R() => (float)rng.NextDouble();
        void Add(ItemId type, float x, float z, float scale, float sink, float tilt)
        {
            Vector3 nrm = gen.Normal(x, z);
            d.Collectibles.Add((type, Place(new Vector3(x, gen.Height(x, z) - sink, z), R() * Mathf.Tau, Vector3.One * scale, nrm, tilt)));
        }

        // Manganese nodules: patches on flat sediment.
        if (R() < 0.6f)
        {
            float cx = ox + 8 + R() * (Size - 16), cz = oz + 8 + R() * (Size - 16);
            if (gen.Normal(cx, cz).Y > 0.88f && gen.VentField(cx, cz) < 0.3f)
            {
                int n = 2 + (int)(R() * 5);
                for (int i = 0; i < n; i++)
                {
                    float a = R() * Mathf.Tau, r = R() * 7f;
                    Add(ItemId.Nodule, cx + MathF.Cos(a) * r, cz + MathF.Sin(a) * r, 0.9f + R() * 0.5f, 0.1f, 1f);
                }
            }
        }

        // Lattice crystals: on slopes and at spire feet.
        foreach (var s in d.SpireColliders)
            if (R() < 0.6f)
            {
                float a = R() * Mathf.Tau;
                Add(ItemId.Crystal, s.pos.X + MathF.Cos(a) * (s.radius + 1.5f), s.pos.Z + MathF.Sin(a) * (s.radius + 1.5f), 1f + R() * 0.8f, 0.2f, 0.6f);
            }
        for (int i = 0; i < 4; i++)
        {
            float x = ox + R() * Size, z = oz + R() * Size;
            float ny = gen.Normal(x, z).Y;
            if (ny > 0.45f && ny < 0.85f && R() < 0.35f) Add(ItemId.Crystal, x, z, 1f + R() * 0.8f, 0.2f, 0.8f);
        }

        // Lumen pods: in the glowing meadows.
        float dens = gen.FloraDensity(ox + Size * 0.5f, oz + Size * 0.5f);
        int pods = (int)(dens * 6f + R());
        for (int i = 0; i < pods; i++)
        {
            float x = ox + R() * Size, z = oz + R() * Size;
            if (gen.Normal(x, z).Y > 0.8f) Add(ItemId.Lumen, x, z, 1f + R() * 0.6f, 0.05f, 0.3f);
        }

        // Clams: crowd around vents; a few under rocks elsewhere.
        foreach (var v in d.Vents)
        {
            int n = 2 + (int)(R() * 3);
            for (int i = 0; i < n; i++)
            {
                float a = R() * Mathf.Tau, r = 3f + R() * 6f;
                Add(ItemId.Clam, v.X + MathF.Cos(a) * r, v.Z + MathF.Sin(a) * r, 0.9f + R() * 0.4f, 0.05f, 1f);
            }
        }
        if (R() < 0.2f) Add(ItemId.Clam, ox + R() * Size, oz + R() * Size, 1f, 0.05f, 1f);
    }
}
