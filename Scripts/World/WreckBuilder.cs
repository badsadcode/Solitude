using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Systems;

namespace Solitude.World;

/// <summary>
/// Builds a wreck's body procedurally (PS1 blockout, like the boat itself): a supply sub broken in
/// two, a string of cargo containers along a snapped cable, a survey drone nose-down in the mud, an
/// escape capsule with its hatch open. Solid to the boat, the diver and the proximity panel. Reports
/// where the salvage, the recorder and the pinger light go (world space).
/// The root must be added under an identity-transformed parent: pieces are placed in world coordinates.
/// </summary>
public static class WreckBuilder
{
    public sealed class Built
    {
        public Node3D Root;
        public readonly List<Vector3> Salvage = new();
        public Vector3 Recorder;
        public OmniLight3D Pinger;
    }

    private static readonly Color Rust = new(0.42f, 0.2f, 0.13f), Grey = new(0.3f, 0.32f, 0.3f), Dark = new(0.13f, 0.13f, 0.13f),
        Orange = new(0.75f, 0.35f, 0.08f), Yellow = new(0.7f, 0.6f, 0.15f), Growth = new(0.2f, 0.3f, 0.26f);

    public static Built Build(WreckSite w, TerrainGenerator gen)
    {
        var b = new Built { Root = new Node3D { Name = $"Wreck_{w.Id}" } };
        var rng = new Random(w.Seed + 3);
        var body = new StaticBody3D { CollisionLayer = Layers.World, CollisionMask = 0 };
        b.Root.AddChild(body);

        // Settled into the mud: a piece's local origin sits a little below the seabed under it.
        Vector3 Ground(Vector3 local, float sink)
        {
            Vector3 g = w.Position + new Basis(Vector3.Up, w.Yaw) * local;
            g.Y = gen.Height(g.X, g.Z) - sink;
            return g;
        }
        void Piece(ArrayMesh mesh, Transform3D xf, Vector3 boxSize, Vector3 boxOffset)
        {
            var mi = new MeshInstance3D { Mesh = mesh };
            b.Root.AddChild(mi);
            mi.Transform = xf;
            var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = boxSize } };
            body.AddChild(cs);
            cs.Transform = xf * new Transform3D(Basis.Identity, boxOffset);
        }
        Basis Yawed(float extraYaw, float pitch, float roll) =>
            new Basis(Vector3.Up, w.Yaw + extraYaw) * new Basis(Vector3.Right, pitch) * new Basis(Vector3.Back, roll);
        Color Tint(Color c) => w.Wrong ? c.Lerp(Growth, 0.6f) : c;

        switch (w.Kind)
        {
            case WreckKind.SupplySub:
            {
                // Two halves of an 18 m boat, broken at the waist, lying a few metres apart.
                var bow = new List<Vector2> { new(-9f, 0f), new(-8.6f, 1.1f), new(-7.6f, 1.8f), new(-5.5f, 2.1f), new(0.4f, 2.1f) };
                var stern = new List<Vector2> { new(-0.4f, 2.1f), new(5f, 2.1f), new(7.5f, 1.5f), new(9f, 0.6f), new(9.3f, 0f) };
                var mbA = new MeshBuilder { UvScale = 1f };
                mbA.LatheZ(bow, 10, Tint(Grey), 0.9f, (ring, i) => Tint(i % 4 == 0 ? Rust : Grey).Shade(0.85f + (ring % 2) * 0.1f));
                mbA.Box(new Vector3(0, 2.6f, -2.5f), new Vector3(1.3f, 2.0f, 3.5f), Tint(Grey));   // sail
                mbA.Box(new Vector3(0, 0, 0.5f), new Vector3(3.6f, 3.6f, 0.2f), Dark);             // the torn end
                var mbB = new MeshBuilder { UvScale = 1f };
                mbB.LatheZ(stern, 10, Tint(Grey), 0.9f, (ring, i) => Tint(i % 3 == 0 ? Rust : Grey).Shade(0.85f + (ring % 2) * 0.1f));
                mbB.Box(new Vector3(0, 1.9f, 8f), new Vector3(0.14f, 2.2f, 1.6f), Tint(Grey));      // rudder
                mbB.Box(new Vector3(0, 0, -0.5f), new Vector3(3.6f, 3.6f, 0.2f), Dark);
                for (int k = 0; k < 4; k++)
                    mbB.BoxOriented(new Transform3D(new Basis(Vector3.Back, k * Mathf.Pi / 2f + 0.4f), new Vector3(0, 0, 9.6f)), new Vector3(0.25f, 1.4f, 0.08f), Rust);
                var xa = new Transform3D(Yawed(0f, 0.12f, 0.35f), Ground(new Vector3(0, 0, -4f), -1.2f));
                var xb = new Transform3D(Yawed(0.5f, -0.08f, -0.6f), Ground(new Vector3(2.5f, 0, 6f), -1.0f));
                Piece(mbA.Build(Art.Hull), xa, new Vector3(3.8f, 3.6f, 9.4f), new Vector3(0, 0, -4.3f));
                Piece(mbB.Build(Art.Hull), xb, new Vector3(3.8f, 3.6f, 9.6f), new Vector3(0, 0, 4.4f));
                b.Recorder = xa * new Vector3(0, 0.2f, -0.2f);      // in the torn end of the bow half
                b.Salvage.Add(xb * new Vector3(0, 0.3f, 0.2f));
                for (int i = 0; i < 6; i++) b.Salvage.Add(Ground(new Vector3((float)rng.NextDouble() * 14f - 7f, 0, (float)rng.NextDouble() * 22f - 11f), -0.3f));
                b.Pinger = Light(b.Root, xa * new Vector3(0, 3.8f, -2.5f));
                break;
            }
            case WreckKind.CargoString:
            {
                // Containers along a snapped tether, one burst open, the rest half buried.
                int n = 3 + rng.Next(3);
                for (int i = 0; i < n; i++)
                {
                    var mb = new MeshBuilder { UvScale = 0.6f };
                    Color c = Tint(i % 2 == 0 ? Orange : Yellow);
                    bool open = i == 1;
                    mb.Box(new Vector3(0, 1.25f, 0), new Vector3(2.5f, 2.5f, 6f), c.Shade(0.9f));
                    for (int k = -2; k <= 2; k++) mb.Box(new Vector3(0, 1.25f, k * 1.2f), new Vector3(2.6f, 2.6f, 0.12f), c.Shade(0.7f)); // ribs
                    if (open) mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, -1.1f), new Vector3(0, 2.6f, 3.6f)), new Vector3(2.4f, 0.1f, 2.4f), c);
                    var xf = new Transform3D(Yawed(0.2f * i + (float)rng.NextDouble() * 0.4f, 0f, (float)rng.NextDouble() * 0.5f - 0.25f),
                        Ground(new Vector3((float)rng.NextDouble() * 4f - 2f, 0, i * 8f - n * 4f), 0.6f + (float)rng.NextDouble() * 0.6f));
                    Piece(mb.Build(Art.Hull), xf, new Vector3(2.6f, 2.6f, 6f), new Vector3(0, 1.25f, 0));
                    if (open) { b.Salvage.Add(xf * new Vector3(0, 0.8f, 2.6f)); b.Salvage.Add(xf * new Vector3(0, 0.8f, 3.6f)); }
                    b.Salvage.Add(xf * new Vector3(1.6f, 0.3f, (float)rng.NextDouble() * 4f - 2f));
                }
                // The tether: a sagging line of short segments.
                var cable = new MeshBuilder();
                for (int i = 0; i < n * 4; i++) cable.Box(new Vector3(0, 0.15f, i * 2f - n * 4f), new Vector3(0.12f, 0.12f, 2.1f), Dark);
                var cm = new MeshInstance3D { Mesh = cable.Build(Art.Hull) };
                b.Root.AddChild(cm);
                cm.Transform = new Transform3D(Yawed(0.1f, 0f, 0f), Ground(Vector3.Zero, 0.1f));
                b.Recorder = Vector3.Zero;
                break;
            }
            case WreckKind.SurveyDrone:
            {
                // A 6 m torpedo body, nose buried, fins up, a sensor dome cracked.
                var mb = new MeshBuilder { UvScale = 1f };
                var prof = new List<Vector2> { new(-3f, 0f), new(-2.8f, 0.45f), new(-2.2f, 0.6f), new(2.4f, 0.6f), new(3f, 0.2f), new(3.1f, 0f) };
                mb.LatheZ(prof, 8, Tint(Yellow), 1f, (ring, i) => Tint(ring % 2 == 0 ? Yellow : Orange).Shade(0.9f));
                for (int k = 0; k < 4; k++)
                    mb.BoxOriented(new Transform3D(new Basis(Vector3.Back, k * Mathf.Pi / 2f), new Vector3(0, 0, 2.7f)), new Vector3(0.06f, 1.6f, 0.8f), Dark);
                mb.Box(new Vector3(0, -0.5f, -1.2f), new Vector3(0.5f, 0.3f, 0.8f), Dark);   // sensor pod
                var xf = new Transform3D(Yawed(0f, 0.75f, 0.2f), Ground(Vector3.Zero, 0.4f));
                Piece(mb.Build(Art.Hull), xf, new Vector3(1.3f, 1.3f, 6.2f), Vector3.Zero);
                b.Recorder = xf * new Vector3(0, 0.75f, 1.2f);
                for (int i = 0; i < 3; i++) b.Salvage.Add(Ground(new Vector3((float)rng.NextDouble() * 6f - 3f, 0, (float)rng.NextDouble() * 6f - 3f), -0.3f));
                b.Pinger = Light(b.Root, xf * new Vector3(0, 0.7f, 2.6f));
                break;
            }
            default: // Capsule
            {
                var mb = new MeshBuilder { UvScale = 1f };
                var prof = new List<Vector2> { new(-1.6f, 0f), new(-1.4f, 0.9f), new(-0.8f, 1.35f), new(0.8f, 1.35f), new(1.4f, 0.9f), new(1.6f, 0f) };
                mb.LatheZ(prof, 10, Tint(Orange), 1f, (ring, i) => Tint(Orange).Shade(0.85f + (i % 2) * 0.1f));
                mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, -1.2f), new Vector3(0, 1.6f, -0.8f)), new Vector3(0.9f, 0.08f, 0.9f), Tint(Orange)); // hatch, open
                mb.Box(new Vector3(0, 1.3f, 0), new Vector3(0.8f, 0.12f, 0.8f), Dark);   // the hole
                var xf = new Transform3D(Yawed(0f, 0.15f, 0.45f), Ground(Vector3.Zero, -0.6f));
                Piece(mb.Build(Art.Hull), xf, new Vector3(2.7f, 2.7f, 3.2f), Vector3.Zero);
                b.Recorder = xf * new Vector3(0, 1.6f, 0.2f);
                for (int i = 0; i < 2; i++) b.Salvage.Add(Ground(new Vector3((float)rng.NextDouble() * 4f - 2f, 0, (float)rng.NextDouble() * 4f - 2f), -0.3f));
                b.Pinger = Light(b.Root, xf * new Vector3(0, 1.5f, 1.2f));
                break;
            }
        }
        return b;
    }

    private static OmniLight3D Light(Node3D root, Vector3 at)
    {
        var l = new OmniLight3D { LightColor = new Color(1f, 0.15f, 0.08f), LightEnergy = 0f, OmniRange = 12f };
        root.AddChild(l);
        l.Position = at;
        // A small red lens the light sits in.
        var mat = Art.World(Art.FlatTexture(new Color(0.5f, 0.05f, 0.03f)), Colors.White).Glow(new Color(1f, 0.15f, 0.08f), 0f);
        var lens = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f, RadialSegments = 6, Rings = 3 }, MaterialOverride = mat, Name = "PingerLens" };
        l.AddChild(lens);
        return l;
    }

    /// <summary>A piece of salvage the diver can bag: a small battered case (or the recorder, striped).</summary>
    public static Node3D SalvageNode(bool recorder)
    {
        var mb = new MeshBuilder();
        if (recorder)
        {
            mb.Box(Vector3.Zero, new Vector3(0.35f, 0.25f, 0.45f), new Color(0.9f, 0.4f, 0.05f, 0.5f));    // glows a little (alpha)
            mb.Box(new Vector3(0, 0.13f, 0), new Vector3(0.36f, 0.02f, 0.1f), new Color(0.9f, 0.9f, 0.85f));
        }
        else
        {
            mb.Box(Vector3.Zero, new Vector3(0.5f, 0.3f, 0.35f), new Color(0.35f, 0.38f, 0.34f));
            mb.Box(new Vector3(0, 0.16f, 0), new Vector3(0.3f, 0.04f, 0.06f), new Color(0.15f, 0.15f, 0.15f));
            mb.Box(new Vector3(0, 0, 0.18f), new Vector3(0.5f, 0.06f, 0.02f), new Color(0.75f, 0.6f, 0.15f));
        }
        return new MeshInstance3D { Mesh = mb.Build(Art.Hull) };
    }
}
