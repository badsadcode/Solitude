using System;

namespace Solitude.Core;

/// <summary>
/// Seeded 2D gradient (Perlin) noise. Pure C#, no Godot objects, so it is safe to use
/// from worker threads (terrain chunks are generated off the main thread).
/// </summary>
public sealed class Perlin
{
    private readonly int[] _perm = new int[512];

    public Perlin(int seed)
    {
        var rng = new Random(seed);
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Grad(int h, float x, float y) => (h & 7) switch
    {
        0 => x + y,
        1 => -x + y,
        2 => x - y,
        3 => -x - y,
        4 => x,
        5 => -x,
        6 => y,
        _ => -y,
    };

    /// <summary>Single octave, roughly in [-1, 1].</summary>
    public float Get(float x, float y)
    {
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        float xf = x - xi;
        float yf = y - yi;
        xi &= 255;
        yi &= 255;
        float u = Fade(xf);
        float v = Fade(yf);
        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
        return Lerp(x1, x2, v);
    }

    /// <summary>Fractal sum, roughly in [-1, 1].</summary>
    public float Fbm(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Get(x, y) * amp;
            norm += amp;
            amp *= gain;
            x = x * lacunarity + 17.31f;
            y = y * lacunarity - 9.73f;
        }
        return sum / norm;
    }

    /// <summary>Ridged multifractal in [0, 1]; sharp crests near 1.</summary>
    public float Ridged(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0f, amp = 1f, norm = 0f, weight = 1f;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - MathF.Abs(Get(x, y));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2f, 0f, 1f);
            sum += n * amp;
            norm += amp;
            amp *= gain;
            x = x * lacunarity + 5.17f;
            y = y * lacunarity + 3.91f;
        }
        return sum / norm;
    }

    /// <summary>Cheap deterministic hash → [0, 1). Used for scatter placement.</summary>
    public static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
