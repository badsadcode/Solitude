using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.World;

/// <summary>
/// Shared prop meshes for seabed scatter, built once on the main thread.
/// Vertex colour alpha &lt; 1 marks emissive parts (see ps1_world emission_from_alpha).
/// </summary>
public static class Props
{
    public static ArrayMesh[] Rocks;
    public static ArrayMesh Spire;
    public static ArrayMesh Stalk;
    public static ArrayMesh Fan;
    public static ArrayMesh Chimney;
    public static ArrayMesh WhaleFall;
    public static ArrayMesh TubeWorms;

    private static bool _ready;

    public static void Init()
    {
        if (_ready) return;
        _ready = true;

        Rocks = new ArrayMesh[3];
        for (int i = 0; i < Rocks.Length; i++)
        {
            var mb = new MeshBuilder();
            mb.Rock(Vector3.Zero, new Vector3(1f, 0.7f + i * 0.15f, 1.1f), new Color(0.33f, 0.31f, 0.3f), 100 + i, 0.35f);
            Rocks[i] = mb.Build(Art.RockMat);
        }

        Spire = BuildSpire();
        Stalk = BuildStalk();
        Fan = BuildFan();
        Chimney = BuildChimney();
        WhaleFall = BuildWhaleFall();
        TubeWorms = BuildTubeWorms();
    }

    /// <summary>Unit spire: radius ~1, height 1, stacked jittered prisms.</summary>
    private static ArrayMesh BuildSpire()
    {
        var mb = new MeshBuilder();
        const int seg = 6, layers = 7;
        var col = new Color(0.27f, 0.25f, 0.24f);
        for (int l = 0; l < layers; l++)
        {
            float y0 = (float)l / layers, y1 = (float)(l + 1) / layers;
            float r0 = 1f - y0 * 0.75f, r1 = 1f - y1 * 0.75f;
            float off = (Perlin.Hash(l, 3, 9) - 0.5f) * 0.25f;
            float shade = 0.85f + Perlin.Hash(l, 7, 2) * 0.3f;
            mb.Xform = new Transform3D(Basis.Identity, new Vector3(off, 0, -off * 0.5f));
            mb.Cylinder(new Vector3(0, y0, 0), r0 * 1.08f, r1, y1 - y0, seg, col.Shade(shade), caps: l == layers - 1);
        }
        mb.Xform = Transform3D.Identity;
        return mb.Build(Art.RockMat);
    }

    /// <summary>Bioluminescent "lantern stalk": tall thin stalk with glowing bulbs. Height ~1.</summary>
    private static ArrayMesh BuildStalk()
    {
        var mb = new MeshBuilder();
        var stem = new Color(0.2f, 0.28f, 0.25f);
        mb.Cylinder(Vector3.Zero, 0.04f, 0.02f, 1f, 4, stem, caps: false);
        var glow = new Color(0.55f, 1f, 0.9f, 0.5f);
        mb.Rock(new Vector3(0, 1.02f, 0), new Vector3(0.07f, 0.1f, 0.07f), glow, 3, 0.1f);
        mb.Rock(new Vector3(0.03f, 0.7f, 0), new Vector3(0.04f, 0.05f, 0.04f), glow, 4, 0.1f);
        // little fronds
        for (int i = 0; i < 3; i++)
        {
            float a = i * 2.1f;
            Vector3 d = new(MathF.Cos(a), 0, MathF.Sin(a));
            Vector3 b = new(0, 0.3f + i * 0.15f, 0);
            mb.Tri(b, b + d * 0.18f + Vector3.Up * 0.12f, b + Vector3.Up * 0.08f, stem.Shade(1.3f), d);
            mb.Tri(b, b + Vector3.Up * 0.08f, b + d * 0.18f + Vector3.Up * 0.12f, stem.Shade(1.3f), -d);
        }
        return mb.Build(Art.Flora);
    }

    /// <summary>Flat fan coral-like organism, double sided. Size ~1.</summary>
    private static ArrayMesh BuildFan()
    {
        var mb = new MeshBuilder();
        var body = new Color(0.55f, 0.18f, 0.12f);
        var tip = new Color(1f, 0.55f, 0.3f, 0.5f);
        const int seg = 7;
        mb.Cylinder(Vector3.Zero, 0.05f, 0.04f, 0.25f, 4, body, caps: false);
        Vector3 root = new(0, 0.25f, 0);
        for (int i = 0; i < seg; i++)
        {
            float a0 = Mathf.Pi * (0.1f + 0.8f * i / seg), a1 = Mathf.Pi * (0.1f + 0.8f * (i + 1) / seg);
            Vector3 p0 = root + new Vector3(MathF.Cos(a0) * 0.6f, MathF.Sin(a0) * 0.75f, 0);
            Vector3 p1 = root + new Vector3(MathF.Cos(a1) * 0.6f, MathF.Sin(a1) * 0.75f, 0);
            Color c = (i % 2 == 0) ? body : body.Shade(1.2f);
            mb.Tri(root, p0, p1, c, Vector3.Back);
            mb.Tri(root, p0, p1, c, Vector3.Forward);
            Vector3 e = (p0 + p1) * 0.5f + (p0 - root).Normalized() * 0.06f;
            mb.Tri(p0, p1, e, tip, Vector3.Back);
            mb.Tri(p0, p1, e, tip, Vector3.Forward);
        }
        return mb.Build(Art.FloraWarm);
    }

    /// <summary>Hydrothermal chimney with a glowing throat. Height ~1.</summary>
    private static ArrayMesh BuildChimney()
    {
        var mb = new MeshBuilder();
        var rock = new Color(0.16f, 0.14f, 0.13f);
        mb.Cylinder(Vector3.Zero, 0.45f, 0.3f, 0.5f, 6, rock, caps: false);
        mb.Cylinder(new Vector3(0.04f, 0.5f, 0), 0.32f, 0.2f, 0.45f, 6, rock.Shade(1.2f), caps: false);
        mb.Cylinder(new Vector3(0.06f, 0.95f, 0), 0.2f, 0.16f, 0.05f, 6, new Color(1f, 0.6f, 0.2f, 0.3f), caps: true);
        mb.Rock(new Vector3(0.3f, 0.15f, 0.2f), new Vector3(0.25f, 0.2f, 0.25f), rock, 8);
        return mb.Build(Art.Vent);
    }

    /// <summary>Cluster of tube worms with glowing plumes, used around vents.</summary>
    private static ArrayMesh BuildTubeWorms()
    {
        var mb = new MeshBuilder();
        for (int i = 0; i < 9; i++)
        {
            float a = Perlin.Hash(i, 1, 4) * Mathf.Tau, r = Perlin.Hash(i, 2, 4) * 0.5f;
            Vector3 p = new(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            float h = 0.4f + Perlin.Hash(i, 3, 4) * 0.6f;
            mb.Cylinder(p, 0.035f, 0.03f, h, 4, new Color(0.85f, 0.82f, 0.75f), caps: false);
            mb.Rock(p + Vector3.Up * h, new Vector3(0.06f, 0.05f, 0.06f), new Color(1f, 0.2f, 0.25f, 0.5f), i, 0.2f);
        }
        return mb.Build(Art.FloraWarm);
    }

    /// <summary>
    /// The carcass of something very large. Spine with ribs, ~40 m long.
    /// Shows up rarely. It is mostly there to make the player feel small.
    /// </summary>
    private static ArrayMesh BuildWhaleFall()
    {
        var mb = new MeshBuilder();
        var bone = new Color(0.8f, 0.78f, 0.68f);
        const int vertebrae = 18;
        for (int i = 0; i < vertebrae; i++)
        {
            float z = -20f + i * 2.3f;
            float y = 0.6f + MathF.Sin(i * 0.35f) * 0.6f;
            float s = 1f - MathF.Abs(i - 6f) / 16f;
            mb.Rock(new Vector3(0, y, z), new Vector3(0.7f, 0.55f, 0.8f) * s, bone, i, 0.15f);
            if (i > 2 && i < 14)
            {
                float ribLen = 7f * s + 2f;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 prev = new(0.3f * side, y, z);
                    const int segs = 6;
                    for (int k = 1; k <= segs; k++)
                    {
                        float t = (float)k / segs;
                        float ang = t * Mathf.Pi * 0.85f;
                        // arc outward then down, slightly collapsed
                        Vector3 next = new(side * MathF.Sin(ang) * ribLen * 0.55f, y + MathF.Cos(ang) * ribLen * 0.45f - t * 1.2f, z + t * 0.8f);
                        Vector3 dir = next - prev;
                        var basis = Basis.LookingAt(dir.Normalized(), Vector3.Up);
                        float th = 0.22f * (1f - t * 0.6f);
                        mb.BoxOriented(new Transform3D(basis, (prev + next) * 0.5f), new Vector3(th, th, dir.Length()), bone.Shade(0.9f + t * 0.1f));
                        prev = next;
                    }
                }
            }
        }
        // skull
        mb.Rock(new Vector3(0, 1.4f, -23f), new Vector3(2.2f, 1.5f, 3.2f), bone.Shade(0.95f), 99, 0.25f);
        mb.Rock(new Vector3(0, 0.4f, -25.5f), new Vector3(1.4f, 0.5f, 2.6f), bone.Shade(0.9f), 98, 0.2f);
        return mb.Build(Art.Bone);
    }
}
