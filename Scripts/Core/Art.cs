using System;
using System.Collections.Generic;
using Godot;

namespace Solitude.Core;

/// <summary>
/// Procedural textures and the shared material palette. Everything is generated at
/// startup at tiny resolutions and sampled with nearest filtering for the PS1 look.
/// Main thread only (creates Godot resources).
/// </summary>
public static class Art
{
    private static Shader _world;
    private static readonly Dictionary<string, Material> Cache = new();

    public static Shader WorldShader => _world ??= GD.Load<Shader>("res://Shaders/ps1_world.gdshader");

    // ---------------------------------------------------------------- textures

    private static ImageTexture Make(int w, int h, Func<int, int, Color> f)
    {
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, f(x, y).Clamp());
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Grey speckled rock / sediment detail; multiplied with vertex colour.</summary>
    public static ImageTexture RockTexture(int seed)
    {
        var n = new Perlin(seed);
        return Make(64, 64, (x, y) =>
        {
            float v = 0.78f + n.Fbm(x * 0.11f, y * 0.11f, 3) * 0.35f + (Perlin.Hash(x, y, seed) - 0.5f) * 0.12f;
            float strata = MathF.Sin(y * 0.55f + n.Get(x * 0.05f, y * 0.05f) * 4f) * 0.05f;
            v += strata;
            return new Color(v, v * 0.98f, v * 0.95f);
        });
    }

    public static ImageTexture SedimentTexture(int seed)
    {
        return Make(32, 32, (x, y) =>
        {
            float v = 0.86f + (Perlin.Hash(x, y, seed) - 0.5f) * 0.22f;
            return new Color(v, v, v * 0.97f);
        });
    }

    /// <summary>Riveted metal plates with grime streaks. 64px = 1 tile.</summary>
    public static ImageTexture PanelTexture(int seed, Color baseCol)
    {
        var n = new Perlin(seed);
        return Make(64, 64, (x, y) =>
        {
            float v = 1f;
            int px = x % 32, py = y % 32;
            if (px == 0 || py == 0) v = 0.55f;
            else if (px == 1 || py == 1) v = 1.12f;
            if ((px == 4 || px == 28) && (py == 4 || py == 28)) v = 1.25f;
            if ((px == 5 || px == 29) && (py == 5 || py == 29)) v = 0.6f;
            float grime = n.Fbm(x * 0.3f, y * 0.04f, 2) * 0.18f;
            v += grime + (Perlin.Hash(x, y, seed) - 0.5f) * 0.06f;
            return baseCol * v;
        });
    }

    public static ImageTexture GratingTexture()
    {
        return Make(32, 32, (x, y) =>
        {
            int px = x % 8, py = y % 8;
            bool bar = px < 2 || py < 2;
            float v = bar ? 0.75f + (px == 0 || py == 0 ? 0.25f : 0f) : 0.1f;
            return new Color(v * 0.9f, v * 0.9f, v * 0.85f);
        });
    }

    public static ImageTexture HullTexture(int seed)
    {
        var n = new Perlin(seed);
        return Make(64, 64, (x, y) =>
        {
            float v = 1f;
            if (y % 21 == 0) v = 0.6f;
            if (x % 32 == 0) v = 0.7f;
            if (y % 21 == 2 && x % 6 == 3) v = 1.3f;
            float rust = MathF.Max(0f, n.Fbm(x * 0.08f, y * 0.25f, 3));
            var c = new Color(v, v, v);
            return c.Lerp(new Color(0.75f, 0.42f, 0.22f) * v, Math.Clamp(rust * 1.8f, 0f, 0.8f));
        });
    }

    public static ImageTexture HazardTexture()
    {
        return Make(32, 32, (x, y) => ((x + y) / 8) % 2 == 0 ? new Color(0.9f, 0.7f, 0.1f) : new Color(0.08f, 0.08f, 0.08f));
    }

    public static ImageTexture FlatTexture(Color c) => Make(4, 4, (_, _) => c);

    // ---------------------------------------------------------------- materials

    public static ShaderMaterial World(Texture2D tex, Color tint, float uvScale = 1f, bool triplanar = false,
        float triScale = 0.25f, float roughness = 0.9f, float metallic = 0f, float jitter = 180f)
    {
        var m = new ShaderMaterial { Shader = WorldShader };
        m.SetShaderParameter("albedo_tex", tex);
        m.SetShaderParameter("tint", tint);
        m.SetShaderParameter("uv_scale", uvScale);
        m.SetShaderParameter("triplanar", triplanar);
        m.SetShaderParameter("tri_scale", triScale);
        m.SetShaderParameter("roughness_v", roughness);
        m.SetShaderParameter("metallic_v", metallic);
        m.SetShaderParameter("jitter", jitter);
        return m;
    }

    public static ShaderMaterial Glow(this ShaderMaterial m, Color c, float energy, bool fromAlpha = false, float pulse = 0f)
    {
        m.SetShaderParameter("emission_color", c);
        m.SetShaderParameter("emission_energy", energy);
        m.SetShaderParameter("emission_from_alpha", fromAlpha);
        m.SetShaderParameter("pulse", pulse);
        return m;
    }

    public static ShaderMaterial Sway(this ShaderMaterial m, float amount)
    {
        m.SetShaderParameter("sway", amount);
        return m;
    }

    private static T Cached<T>(string key, Func<T> make) where T : Material
    {
        if (Cache.TryGetValue(key, out var m)) return (T)m;
        var made = make();
        Cache[key] = made;
        return made;
    }

    // Exterior palette
    public static ShaderMaterial Terrain => Cached("terrain", () => World(RockTexture(11), Colors.White, triplanar: true, triScale: 0.18f, jitter: 200f));
    public static ShaderMaterial RockMat => Cached("rock", () => World(RockTexture(23), Colors.White, triplanar: true, triScale: 0.3f));
    public static ShaderMaterial Flora => Cached("flora", () =>
        World(SedimentTexture(5), Colors.White, triplanar: true, triScale: 0.5f).Glow(new Color(0.3f, 1f, 0.85f), 2.2f, fromAlpha: true, pulse: 1.3f).Sway(0.35f));
    public static ShaderMaterial FloraWarm => Cached("flora_warm", () =>
        World(SedimentTexture(6), Colors.White, triplanar: true, triScale: 0.5f).Glow(new Color(1f, 0.45f, 0.2f), 1.6f, fromAlpha: true, pulse: 0.7f).Sway(0.2f));
    public static ShaderMaterial Nodule => Cached("nodule", () =>
        World(RockTexture(61), Colors.White, triplanar: true, triScale: 0.8f, roughness: 0.35f, metallic: 0.6f).Glow(new Color(1f, 0.6f, 0.3f), 0.6f, fromAlpha: true));
    public static ShaderMaterial Crystal => Cached("crystal", () =>
        World(SedimentTexture(71), Colors.White, triplanar: true, triScale: 1f, roughness: 0.15f, metallic: 0.2f).Glow(new Color(0.7f, 0.5f, 1f), 1.4f, fromAlpha: true, pulse: 0.9f));
    public static ShaderMaterial Bone => Cached("bone", () => World(RockTexture(41), new Color(0.82f, 0.8f, 0.7f), triplanar: true, triScale: 0.4f));
    public static ShaderMaterial Vent => Cached("vent", () =>
        World(RockTexture(51), Colors.White, triplanar: true, triScale: 0.4f).Glow(new Color(1f, 0.35f, 0.08f), 3f, fromAlpha: true, pulse: 2.1f));
    public static ShaderMaterial Hull => Cached("hull", () => World(HullTexture(3), Colors.White, uvScale: 0.25f, roughness: 0.6f, metallic: 0.3f).Glow(new Color(1f, 0.8f, 0.45f), 3f, fromAlpha: true));

    // Interior palette
    public static ShaderMaterial Wall => Cached("wall", () => World(PanelTexture(7, new Color(0.55f, 0.58f, 0.52f)), Colors.White, uvScale: 0.6f, roughness: 0.7f, metallic: 0.2f, jitter: 260f));
    public static ShaderMaterial WallDark => Cached("wall_dark", () => World(PanelTexture(8, new Color(0.32f, 0.33f, 0.31f)), Colors.White, uvScale: 0.6f, roughness: 0.6f, metallic: 0.3f, jitter: 260f));
    public static ShaderMaterial Floor => Cached("floor", () => World(GratingTexture(), Colors.White, uvScale: 1.2f, roughness: 0.5f, metallic: 0.5f, jitter: 260f));
    public static ShaderMaterial Hazard => Cached("hazard", () => World(HazardTexture(), Colors.White, uvScale: 1f, jitter: 260f));
    public static ShaderMaterial Machine => Cached("machine", () =>
        World(PanelTexture(9, new Color(0.45f, 0.5f, 0.55f)), Colors.White, uvScale: 1.5f, roughness: 0.5f, metallic: 0.4f, jitter: 260f)
            .Glow(new Color(1f, 1f, 1f), 0.9f, fromAlpha: true));
    public static ShaderMaterial Pipe => Cached("pipe", () => World(FlatTexture(new Color(0.8f, 0.8f, 0.8f)), Colors.White, roughness: 0.4f, metallic: 0.6f, jitter: 260f));

    public static ShaderMaterial Crt(Texture2D feed)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/crt_monitor.gdshader") };
        m.SetShaderParameter("feed", feed);
        return m;
    }
}
