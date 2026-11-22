using System;
using Godot;
using Solitude.Core;

namespace Solitude.World;

/// <summary>
/// The seabed height function and biome fields. Pure maths: deterministic per seed and
/// thread-safe, so chunks can be generated on worker threads.
///
/// Layers (in order):
///   continental swell → shelf drop-offs (huge cliffs into the dark) → ridged mountains →
///   winding canyons → terracing (layered rock) → small detail.
/// Ocean surface is y = 0. Typical seabed: -350 .. -900 m.
/// </summary>
public sealed class TerrainGenerator
{
    public readonly int Seed;
    private readonly Perlin _cont, _shelf, _mount, _mountMask, _canyon, _canyonMask, _warp, _detail, _terrace, _biome, _vent;

    public TerrainGenerator(int seed)
    {
        Seed = seed;
        _cont = new Perlin(seed + 1);
        _shelf = new Perlin(seed + 2);
        _mount = new Perlin(seed + 3);
        _mountMask = new Perlin(seed + 4);
        _canyon = new Perlin(seed + 5);
        _canyonMask = new Perlin(seed + 6);
        _warp = new Perlin(seed + 7);
        _detail = new Perlin(seed + 8);
        _terrace = new Perlin(seed + 9);
        _biome = new Perlin(seed + 10);
        _vent = new Perlin(seed + 11);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public float Height(float x, float z)
    {
        float wx = x + _warp.Get(x * 0.004f, z * 0.004f) * 70f;
        float wz = z + _warp.Get(x * 0.004f + 31.7f, z * 0.004f - 17.3f) * 70f;

        float h = -470f + _cont.Fbm(x * 0.0006f, z * 0.0006f, 3) * 170f;

        // Shelf edges: kilometre-scale cliffs that drop into the abyss.
        float s = _shelf.Fbm(wx * 0.00045f, wz * 0.00045f, 2);
        h -= Perlin.SmoothStep(0.02f, 0.09f, s) * 240f;

        // Ridged mountains, only where the mask allows.
        float mm = Perlin.SmoothStep(-0.1f, 0.45f, _mountMask.Fbm(x * 0.0011f, z * 0.0011f, 2));
        float r = _mount.Ridged(wx * 0.0028f, wz * 0.0028f, 5);
        h += r * r * 290f * mm;

        // Canyons: narrow bands where the noise crosses zero.
        float cm = Perlin.SmoothStep(-0.25f, 0.2f, _canyonMask.Get(x * 0.0007f, z * 0.0007f));
        float cn = MathF.Abs(_canyon.Fbm(wx * 0.0016f, wz * 0.0016f, 2));
        float cs = 1f - Perlin.SmoothStep(0.012f, 0.1f, cn);
        h -= cs * 165f * cm;

        // Terracing: layered rock ledges, strongest in canyon country.
        float tMask = Perlin.SmoothStep(0.05f, 0.55f, _terrace.Get(x * 0.002f, z * 0.002f)) * (0.35f + 0.65f * cm);
        const float step = 13f;
        float t = h / step;
        float fl = MathF.Floor(t);
        float stepped = (fl + Perlin.SmoothStep(0.55f, 0.85f, t - fl)) * step;
        h = Lerp(h, stepped, tMask * 0.8f);

        h += _detail.Fbm(x * 0.03f, z * 0.03f, 2) * 3.5f + _detail.Get(x * 0.13f, z * 0.13f) * 0.7f;
        return h;
    }

    /// <summary>Approximate surface normal by central differences.</summary>
    public Vector3 Normal(float x, float z, float e = 1.5f)
    {
        float hx = Height(x + e, z) - Height(x - e, z);
        float hz = Height(x, z + e) - Height(x, z - e);
        return new Vector3(-hx, 2f * e, -hz).Normalized();
    }

    /// <summary>0..1 — glowing flora meadows.</summary>
    public float FloraDensity(float x, float z) => Perlin.SmoothStep(0.0f, 0.5f, _biome.Fbm(x * 0.004f, z * 0.004f, 2));

    /// <summary>0..1 — hydrothermal vent fields.</summary>
    public float VentField(float x, float z) => Perlin.SmoothStep(0.35f, 0.65f, _vent.Fbm(x * 0.0018f, z * 0.0018f, 2));

    /// <summary>-1..1 — alien sediment colouring.</summary>
    public float Tint(float x, float z) => _biome.Get(x * 0.0021f + 100f, z * 0.0021f - 50f);

    public Color GroundColor(Vector3 centroid, Vector3 normal)
    {
        float slope = 1f - normal.Y;
        float x = centroid.X, z = centroid.Z;
        float n = Perlin.Hash((int)MathF.Floor(x * 0.5f), (int)MathF.Floor(z * 0.5f), Seed) - 0.5f;

        Color sediment = new(0.42f, 0.41f, 0.36f);
        float tint = Tint(x, z);
        if (tint > 0.25f) sediment = sediment.Lerp(new Color(0.37f, 0.3f, 0.45f), Math.Clamp((tint - 0.25f) * 3f, 0f, 1f));
        else if (tint < -0.3f) sediment = sediment.Lerp(new Color(0.52f, 0.43f, 0.22f), Math.Clamp((-tint - 0.3f) * 3f, 0f, 1f));

        Color rock = new(0.29f, 0.27f, 0.26f);
        // Layer banding on cliffs.
        float band = MathF.Sin(centroid.Y * 0.45f) * 0.5f + 0.5f;
        rock = rock.Lerp(new Color(0.38f, 0.29f, 0.23f), band * 0.5f);

        float vent = VentField(x, z);
        if (vent > 0.01f)
        {
            sediment = sediment.Lerp(new Color(0.12f, 0.11f, 0.1f), vent);
            rock = rock.Lerp(new Color(0.1f, 0.09f, 0.09f), vent);
        }

        Color c = sediment.Lerp(rock, Perlin.SmoothStep(0.22f, 0.42f, slope));
        // Deep places get colder and darker.
        float deep = Perlin.SmoothStep(-500f, -800f, centroid.Y);
        c = c.Lerp(new Color(c.R * 0.6f, c.G * 0.7f, c.B * 0.85f), deep);
        c = new Color(c.R + n * 0.05f, c.G + n * 0.05f, c.B + n * 0.05f, 1f);
        return c;
    }
}
