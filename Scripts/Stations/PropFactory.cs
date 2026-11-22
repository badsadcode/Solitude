using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Stations;

/// <summary>
/// Turns a NoorLevel `prop` marker into something standing in the station.
///
/// If Tom has modelled the class, his asset is used: res://assets/props/&lt;class&gt;_&lt;n&gt;.glb
/// (variant picks n, wrapping round however many he made; &lt;class&gt;_&lt;n&gt;_broken.glb when the
/// marker is tagged broken and that file exists). Asset convention: origin at the marker's origin
/// (floor: footprint centre on the floor; wall: bottom centre of the back face on the wall; ceiling:
/// top centre), front facing +Z in Godot (−Y in Blender's front view), real size ≈ the footprint.
///
/// Otherwise a low-poly placeholder is generated from the class and footprint, in the same frame:
/// width along X, depth along +Z (forward), height along +Y. Vertex alpha &lt; 1 glows (screens, LEDs).
/// </summary>
public static class PropFactory
{
    private static readonly Dictionary<string, int> _variants = new();

    /// <summary>How many asset variants exist for a class (0 = none, use a placeholder).</summary>
    private static int Variants(string cls)
    {
        if (_variants.TryGetValue(cls, out int n)) return n;
        n = 0;
        while (n < 16 && ResourceLoader.Exists($"res://assets/props/{cls}_{n}.glb")) n++;
        _variants[cls] = n;
        return n;
    }

    public static Node3D Make(string cls, string mount, Vector3 fp, int variant, bool broken, string text, Random rng)
    {
        int count = Variants(cls);
        if (count > 0)
        {
            string path = $"res://assets/props/{cls}_{variant % count}";
            if (broken && ResourceLoader.Exists(path + "_broken.glb")) path += "_broken";
            return GD.Load<PackedScene>(path + ".glb").Instantiate<Node3D>();
        }
        var root = new Node3D { Name = $"prop_{cls}" };
        var mb = new MeshBuilder();
        Build(mb, cls, mount, fp, broken, rng);
        root.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        if (cls == "sign" && !string.IsNullOrEmpty(text))
            root.AddChild(new Label3D
            {
                Text = text.ToUpperInvariant(), Position = new Vector3(0, fp.Y * 0.5f, fp.Z + 0.012f), FontSize = 28, PixelSize = 0.0032f,
                Modulate = broken ? new Color(0.5f, 0.5f, 0.45f) : new Color(0.85f, 0.9f, 0.8f), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            });
        return root;
    }

    /// <summary>Should the player bump into it? (Floor furniture with some height, not runs or decals.)</summary>
    public static bool Solid(string cls, string mount, Vector3 fp, bool flat) =>
        mount == "floor" && !flat && fp.Y >= 0.4f && cls is not ("debris" or "bones" or "stain" or "cable_run" or "pipe_run");

    // ------------------------------------------------------------------ placeholders

    private static readonly Color Metal = new(0.42f, 0.45f, 0.46f), Dark = new(0.16f, 0.17f, 0.18f), Paint = new(0.55f, 0.6f, 0.5f),
        Rust = new(0.42f, 0.26f, 0.16f), Bone = new(0.8f, 0.76f, 0.66f), Fabric = new(0.3f, 0.33f, 0.36f);
    private static Color Glow(Color c) => new(c.R, c.G, c.B, 0.5f);

    private static void Build(MeshBuilder m, string cls, string mount, Vector3 fp, bool broken, Random rng)
    {
        float w = Mathf.Max(0.1f, fp.X), d = Mathf.Max(0.05f, fp.Z), h = Mathf.Max(0.02f, fp.Y);
        Color body = broken ? Rust : Metal;
        // Wall pieces sit in front of the wall (origin on the wall, reaching d into the room).
        float zc = mount == "wall" ? d / 2f : 0f;
        float y0 = mount == "ceiling" ? -h : 0f;
        Vector3 C(float x, float y, float z) => new(x, y0 + y, zc + z);

        switch (cls)
        {
            case "console":
                m.Box(C(0, h * 0.35f, 0), new Vector3(w, h * 0.7f, d), body);
                m.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.6f), C(0, h * 0.82f, -d * 0.1f)), new Vector3(w * 0.95f, h * 0.35f, 0.06f), Dark);
                m.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.6f), C(0, h * 0.84f, -d * 0.07f)), new Vector3(w * 0.7f, h * 0.22f, 0.02f),
                    broken ? new Color(0.05f, 0.06f, 0.05f) : Glow(new Color(0.3f, 0.9f, 0.5f)));
                for (int i = 0; i < 5; i++)
                    m.Box(C(-w * 0.4f + i * w * 0.2f, h * 0.68f, d * 0.42f), new Vector3(0.05f, 0.03f, 0.05f), i % 2 == 0 && !broken ? Glow(new Color(1f, 0.6f, 0.2f)) : Dark);
                break;
            case "desk": case "table": case "bench_lab": case "workbench":
                m.Box(C(0, h - 0.03f, 0), new Vector3(w, 0.06f, d), cls == "bench_lab" ? new Color(0.75f, 0.78f, 0.76f) : body);
                foreach (var sx in new[] { -1f, 1f }) foreach (var sz in new[] { -1f, 1f })
                    m.Box(C(sx * (w / 2f - 0.05f), (h - 0.06f) / 2f, sz * (d / 2f - 0.05f)), new Vector3(0.05f, h - 0.06f, 0.05f), Dark);
                if (cls == "bench_lab") m.Box(C(w * 0.25f, h + 0.08f, 0), new Vector3(0.2f, 0.16f, 0.12f), Glow(new Color(0.4f, 0.8f, 0.9f)));
                if (cls == "workbench") m.Box(C(-w * 0.3f, h + 0.06f, 0), new Vector3(0.3f, 0.1f, 0.2f), Rust);
                break;
            case "chair": case "stool":
                m.Box(C(0, h * 0.5f, 0), new Vector3(w * 0.9f, 0.06f, d * 0.9f), Fabric);
                m.Box(C(0, h * 0.25f, 0), new Vector3(0.06f, h * 0.5f, 0.06f), Dark);
                if (cls == "chair") m.Box(C(0, h * 0.78f, -d * 0.42f), new Vector3(w * 0.9f, h * 0.5f, 0.06f), Fabric);
                break;
            case "locker":
                m.Box(C(0, h / 2f, 0), new Vector3(w, h, d), broken ? Rust : Paint);
                m.Box(C(0, h / 2f, d / 2f + 0.005f), new Vector3(0.01f, h * 0.95f, 0.01f), Dark);
                for (int i = 0; i < 3; i++) m.Box(C(0, h * 0.85f - i * 0.05f, d / 2f + 0.005f), new Vector3(w * 0.5f, 0.015f, 0.01f), Dark);
                break;
            case "bunk": case "bed":
                for (int tier = 0; tier < (cls == "bunk" ? 2 : 1); tier++)
                {
                    float ty = 0.35f + tier * (h - 0.4f) * 0.75f;
                    m.Box(C(0, ty, 0), new Vector3(w, 0.08f, d), Dark);
                    m.Box(C(0, ty + 0.08f, 0), new Vector3(w * 0.95f, 0.1f, d * 0.92f), broken ? new Color(0.35f, 0.28f, 0.2f) : Fabric);
                }
                foreach (var sx in new[] { -1f, 1f }) foreach (var sz in new[] { -1f, 1f })
                    m.Box(C(sx * (w / 2f - 0.03f), h / 2f, sz * (d / 2f - 0.03f)), new Vector3(0.05f, h, 0.05f), Metal);
                break;
            case "crate":
                m.Box(C(0, h / 2f, 0), new Vector3(w, h, d), broken ? Rust : new Color(0.35f, 0.4f, 0.3f));
                m.Box(C(0, h / 2f, 0), new Vector3(w * 1.02f, h * 0.1f, d * 1.02f), Dark);
                break;
            case "barrel":
                m.Cylinder(C(0, 0, 0), w / 2f, w / 2f, h, 8, broken ? Rust : new Color(0.5f, 0.35f, 0.15f));
                break;
            case "shelf": case "grow_rack": case "server_rack":
                foreach (var sx in new[] { -1f, 1f }) m.Box(C(sx * (w / 2f - 0.03f), h / 2f, 0), new Vector3(0.05f, h, d), Metal);
                for (int i = 0; i < 4; i++)
                {
                    float sy = 0.15f + i * (h - 0.25f) / 3f;
                    m.Box(C(0, sy, 0), new Vector3(w, 0.04f, d), Dark);
                    if (cls == "grow_rack" && !broken) m.Box(C(0, sy + 0.08f, 0), new Vector3(w * 0.85f, 0.12f, d * 0.7f), Glow(new Color(0.3f, 0.8f, 0.3f)));
                    if (cls == "server_rack") for (int k = 0; k < 4; k++)
                        m.Box(C(-w * 0.3f + k * w * 0.2f, sy + 0.1f, d / 2f), new Vector3(0.03f, 0.03f, 0.02f), !broken && rng.NextDouble() < 0.6 ? Glow(new Color(0.3f, 1f, 0.4f)) : Dark);
                    if (cls == "shelf" && rng.NextDouble() < 0.7) m.Box(C((float)(rng.NextDouble() - 0.5) * w * 0.6f, sy + 0.1f, 0), new Vector3(0.25f, 0.18f, d * 0.6f), Rust);
                }
                break;
            case "specimen_tank":
                m.Cylinder(C(0, 0, 0), w / 2f, w / 2f, 0.2f, 10, Dark);
                m.Cylinder(C(0, 0.2f, 0), w * 0.45f, w * 0.45f, h - 0.4f, 10, broken ? new Color(0.1f, 0.12f, 0.12f) : Glow(new Color(0.15f, 0.45f, 0.4f)));
                m.Cylinder(C(0, h - 0.2f, 0), w / 2f, w / 2f, 0.2f, 10, Dark);
                if (!broken) m.Rock(C(0, h * 0.5f, 0), new Vector3(w * 0.2f, h * 0.18f, w * 0.15f), new Color(0.35f, 0.3f, 0.28f), rng.Next(), 0.5f);
                break;
            case "pump": case "reactor_core":
                m.Cylinder(C(0, 0, 0), w * 0.45f, w * 0.4f, h * 0.85f, 10, body);
                m.Cylinder(C(0, h * 0.85f, 0), w * 0.2f, w * 0.2f, h * 0.15f, 8, Dark);
                if (cls == "reactor_core") for (int i = 0; i < 3; i++)
                    m.Cylinder(C(0, h * (0.2f + i * 0.22f), 0), w * 0.47f, w * 0.47f, 0.05f, 10, broken ? Dark : Glow(new Color(0.4f, 0.7f, 1f)));
                break;
            case "valve_wheel":
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.Tau / 8f;
                    m.BoxOriented(new Transform3D(new Basis(Vector3.Back, a), C(Mathf.Cos(a) * w * 0.4f, h / 2f + Mathf.Sin(a) * h * 0.4f, d * 0.6f)), new Vector3(0.04f, w * 0.32f, 0.04f), new Color(0.6f, 0.15f, 0.1f));
                }
                m.Box(C(0, h / 2f, d * 0.3f), new Vector3(0.08f, 0.08f, d * 0.6f), Metal);
                break;
            case "pipe_run": case "cable_run":
            {
                float r = cls == "pipe_run" ? Mathf.Min(h, d) * 0.4f : 0.03f;
                int strands = cls == "pipe_run" ? 1 : 3;
                for (int k = 0; k < strands; k++)
                    m.BoxOriented(new Transform3D(Basis.Identity, C(0, h / 2f + (k - 1) * 0.05f * (strands > 1 ? 1 : 0), (k - 1) * 0.04f * (strands > 1 ? 1 : 0))),
                        new Vector3(w, r * 2f, r * 2f), cls == "pipe_run" ? body : Dark);
                break;
            }
            case "vent_grate":
                for (int i = 0; i < 6; i++) m.Box(C(0, h * (i + 0.5f) / 6f, d / 2f), new Vector3(w, h / 12f, d * 0.4f), Dark);
                break;
            case "debris":
                for (int i = 0; i < 5; i++)
                    m.BoxOriented(new Transform3D(new Basis(Vector3.Up, (float)rng.NextDouble() * 3f) * new Basis(Vector3.Right, (float)rng.NextDouble() * 0.6f),
                        C((float)(rng.NextDouble() - 0.5) * w, 0.05f, (float)(rng.NextDouble() - 0.5) * d)), new Vector3(0.1f + (float)rng.NextDouble() * 0.4f, 0.04f, 0.1f + (float)rng.NextDouble() * 0.3f), i % 2 == 0 ? Rust : Metal);
                break;
            case "bones":
                for (int i = 0; i < 6; i++)
                    m.BoxOriented(new Transform3D(new Basis(Vector3.Up, (float)rng.NextDouble() * 3f), C((float)(rng.NextDouble() - 0.5) * w * 0.8f, 0.03f, (float)(rng.NextDouble() - 0.5) * d * 0.8f)), new Vector3(0.3f, 0.035f, 0.035f), Bone);
                m.Rock(C(w * 0.2f, 0.08f, 0), new Vector3(0.09f, 0.08f, 0.1f), Bone, rng.Next(), 0.2f);
                break;
            case "stain":
                m.Box(C(0, 0.005f, 0), new Vector3(w, 0.004f, d), new Color(0.12f, 0.08f, 0.06f));
                break;
            case "light_fixture_dead":
                m.Box(C(0, h * 0.5f, 0), new Vector3(w, h, d), Dark);
                if (broken) m.BoxOriented(new Transform3D(new Basis(Vector3.Back, 0.5f), C(w * 0.3f, -0.15f, 0)), new Vector3(0.02f, 0.3f, 0.02f), Dark);
                break;
            case "sign":
                m.Box(C(0, h / 2f, 0), new Vector3(w, h, d), broken ? Dark : new Color(0.2f, 0.25f, 0.22f));
                break;
            case "dive_suit":
                // Hung on the wall like someone who gave up waiting.
                m.Box(C(0, 1.45f, 0), new Vector3(0.36f, 0.34f, 0.34f), new Color(0.55f, 0.5f, 0.3f));
                m.Box(C(0, 1.47f, 0.17f), new Vector3(0.2f, 0.14f, 0.02f), new Color(0.05f, 0.06f, 0.07f));
                m.Box(C(0, 1.0f, 0), new Vector3(0.5f, 0.6f, 0.3f), new Color(0.55f, 0.5f, 0.3f));
                m.Box(C(-0.12f, 0.4f, 0), new Vector3(0.18f, 0.65f, 0.2f), new Color(0.5f, 0.45f, 0.28f));
                m.Box(C(0.12f, 0.4f, 0), new Vector3(0.18f, 0.65f, 0.2f), new Color(0.5f, 0.45f, 0.28f));
                break;
            default:
                m.Box(C(0, h / 2f, 0), new Vector3(w, h, d), body);
                break;
        }
    }
}
