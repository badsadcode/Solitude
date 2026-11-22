using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Sub;

/// <summary>
/// Procedural exterior of the boat: ~33 m, two-tone hull, sail with planes, cruciform
/// stern, shrouded propeller, ballast pods, sonar chin, floodlight housings, camera pods,
/// glowing portholes and a folded manipulator arm. Forward is -Z, up is +Y.
/// Not a spaceship, not a modern sub: welded plates, rust, bolted-on equipment.
/// </summary>
public static class SubmarineModel
{
    public const float HullRadius = 3.1f;
    public const float Squash = 0.92f;

    private static readonly Color Upper = new(0.3f, 0.34f, 0.32f);
    private static readonly Color Lower = new(0.42f, 0.2f, 0.15f);
    private static readonly Color Dark = new(0.14f, 0.15f, 0.15f);
    private static readonly Color Brass = new(0.55f, 0.45f, 0.25f);
    private static readonly Color PortGlow = new(1f, 0.8f, 0.45f, 0.4f);
    private static readonly Color Lens = new(0.9f, 0.95f, 1f, 0.3f);

    public static ArrayMesh BuildHull()
    {
        var mb = new MeshBuilder { UvScale = 1f };
        const int seg = 12;
        var profile = new List<Vector2>
        {
            new(-16.6f, 0f), new(-16.3f, 1.2f), new(-15.6f, 2.1f), new(-14.4f, 2.7f), new(-12.5f, 3.05f),
            new(-10f, 3.1f), new(8f, 3.1f), new(10.5f, 2.8f), new(12.8f, 2.1f), new(14.8f, 1.3f), new(15.8f, 0.7f), new(16.1f, 0f),
        };
        mb.LatheZ(profile, seg, Upper, Squash, (ring, i) =>
        {
            float a = Mathf.Tau * i / seg + Mathf.Pi / seg + Mathf.Pi / seg;
            bool lower = Mathf.Sin(a) < -0.15f;
            float shade = 0.9f + (ring % 2) * 0.08f;
            return (lower ? Lower : Upper).Shade(shade);
        });

        // Sail / conning tower
        mb.Box(new Vector3(0, 4.1f, -3.2f), new Vector3(1.8f, 3.0f, 5.6f), Upper.Shade(1.05f));
        mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.55f), new Vector3(0, 3.95f, -6.4f)), new Vector3(1.78f, 3.0f, 1.4f), Upper.Shade(1.05f));
        mb.Box(new Vector3(0, 5.65f, -3.4f), new Vector3(1.5f, 0.15f, 4.6f), Dark);
        // Sail planes
        mb.Box(new Vector3(-1.9f, 4.4f, -4.2f), new Vector3(2.2f, 0.12f, 1.3f), Upper.Shade(0.9f));
        mb.Box(new Vector3(1.9f, 4.4f, -4.2f), new Vector3(2.2f, 0.12f, 1.3f), Upper.Shade(0.9f));
        // Mast and antenna
        mb.Cylinder(new Vector3(0, 5.7f, -2.2f), 0.12f, 0.08f, 2.2f, 5, Dark);
        mb.Cylinder(new Vector3(0, 5.7f, -1.4f), 0.06f, 0.03f, 3.4f, 4, Dark);
        mb.Box(new Vector3(0, 7.2f, -2.2f), new Vector3(0.6f, 0.25f, 0.25f), Dark);

        // Cruciform stern control surfaces
        mb.Box(new Vector3(0, 2.6f, 13.2f), new Vector3(0.16f, 3.0f, 2.4f), Upper);
        mb.Box(new Vector3(0, -2.6f, 13.2f), new Vector3(0.16f, 3.0f, 2.4f), Lower);
        mb.Box(new Vector3(-2.6f, 0, 13.2f), new Vector3(3.0f, 0.16f, 2.4f), Upper.Shade(0.95f));
        mb.Box(new Vector3(2.6f, 0, 13.2f), new Vector3(3.0f, 0.16f, 2.4f), Upper.Shade(0.95f));

        // Propeller shroud (octagonal duct)
        const int duct = 8;
        for (int i = 0; i < duct; i++)
        {
            float a = Mathf.Tau * i / duct;
            var basis = new Basis(Vector3.Back, a);
            Vector3 p = basis * new Vector3(0, 1.75f, 0) + new Vector3(0, 0, 16.6f);
            mb.BoxOriented(new Transform3D(basis, p), new Vector3(1.45f, 0.14f, 1.6f), Dark.Shade(1.4f));
        }
        for (int i = 0; i < 4; i++)
        {
            var basis = new Basis(Vector3.Back, Mathf.Pi / 4f + i * Mathf.Pi / 2f);
            mb.BoxOriented(new Transform3D(basis, basis * new Vector3(0, 1.1f, 0) + new Vector3(0, 0, 16.3f)), new Vector3(0.1f, 1.3f, 0.5f), Dark);
        }

        // Ballast pods
        foreach (int side in new[] { -1, 1 })
        {
            mb.Xform = new Transform3D(Basis.Identity, new Vector3(side * 2.75f, -1.35f, 0));
            mb.LatheZ(new List<Vector2> { new(-9f, 0f), new(-8.4f, 0.5f), new(-7f, 0.7f), new(6f, 0.7f), new(7.6f, 0.45f), new(8.2f, 0f) }, 8, Lower.Shade(0.8f));
            mb.Xform = Transform3D.Identity;
            // Straps
            for (int s = 0; s < 4; s++)
                mb.Box(new Vector3(side * 2.75f, -1.35f, -6f + s * 4f), new Vector3(1.55f, 1.55f, 0.18f), Dark);
        }

        // Sonar chin dome
        mb.Rock(new Vector3(0, -2.3f, -12f), new Vector3(1.1f, 0.7f, 1.9f), new Color(0.2f, 0.22f, 0.2f), 5, 0.04f);

        // Floodlight housings
        foreach (int side in new[] { -1, 1 })
        {
            Vector3 c = new(side * 1.5f, -1.0f, -15.1f);
            mb.Box(c, new Vector3(0.7f, 0.55f, 0.9f), Dark);
            mb.Box(c + new Vector3(0, 0, -0.46f), new Vector3(0.55f, 0.42f, 0.04f), Lens);
            mb.Box(c + new Vector3(0, 0.35f, 0.3f), new Vector3(0.15f, 0.3f, 0.15f), Dark);
        }
        mb.Box(new Vector3(0, -2.95f, -5.5f), new Vector3(0.7f, 0.4f, 0.7f), Dark);
        mb.Box(new Vector3(0, -3.17f, -5.5f), new Vector3(0.5f, 0.04f, 0.5f), Lens);

        // Camera pods (positions mirror Submarine.CameraMounts)
        foreach (var (pos, dir) in CameraPods)
        {
            mb.Box(pos - dir * 0.2f, new Vector3(0.4f, 0.4f, 0.4f), Brass);
            mb.Box(pos + dir * 0.02f, new Vector3(0.22f, 0.22f, 0.22f) + dir.Abs() * -0.18f, Lens);
        }

        // Glowing portholes along both sides
        for (int i = 0; i < 5; i++)
        {
            float z = -9f + i * 3.6f;
            foreach (int side in new[] { -1, 1 })
            {
                mb.Box(new Vector3(side * 3.05f, 0.75f, z), new Vector3(0.12f, 0.45f, 0.45f), Brass);
                mb.Box(new Vector3(side * 3.1f, 0.75f, z), new Vector3(0.06f, 0.3f, 0.3f), PortGlow);
            }
        }

        // Dive hatch (aft) and lock wheel
        mb.Cylinder(new Vector3(0, 2.7f, 9f), 0.9f, 0.85f, 0.45f, 8, Dark.Shade(1.3f));
        mb.Box(new Vector3(0, 3.2f, 9f), new Vector3(0.9f, 0.08f, 0.08f), Brass);
        mb.Box(new Vector3(0, 3.2f, 9f), new Vector3(0.08f, 0.08f, 0.9f), Brass);

        // Folded manipulator arm under the bow (milestone 3 will unfold it)
        mb.Box(new Vector3(0.9f, -2.65f, -9.5f), new Vector3(0.5f, 0.5f, 0.8f), Brass.Shade(0.8f));
        mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, 0.2f), new Vector3(0.9f, -2.85f, -11f)), new Vector3(0.28f, 0.28f, 2.4f), Upper.Shade(0.8f));
        mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.4f), new Vector3(0.9f, -2.75f, -12.6f)), new Vector3(0.24f, 0.24f, 1.4f), Upper.Shade(0.8f));
        mb.Box(new Vector3(0.9f, -2.5f, -13.3f), new Vector3(0.4f, 0.3f, 0.3f), Dark);

        // Weld seams / hull bands
        for (int i = 0; i < 6; i++)
        {
            float z = -9f + i * 3.4f;
            mb.Box(new Vector3(0, 2.86f, z), new Vector3(0.8f, 0.06f, 0.12f), Dark);
        }
        // Deck grating strip
        mb.Box(new Vector3(0, 2.85f, 4.5f), new Vector3(1.2f, 0.05f, 6f), Dark.Shade(1.2f));

        return mb.Build(Art.Hull);
    }

    public static ArrayMesh BuildPropeller()
    {
        var mb = new MeshBuilder();
        mb.Rock(Vector3.Zero, new Vector3(0.35f, 0.35f, 0.5f), Brass, 7, 0.05f);
        for (int i = 0; i < 5; i++)
        {
            var basis = new Basis(Vector3.Back, Mathf.Tau * i / 5f) * new Basis(Vector3.Up, 0.5f);
            mb.BoxOriented(new Transform3D(basis, new Basis(Vector3.Back, Mathf.Tau * i / 5f) * new Vector3(0, 0.8f, 0)), new Vector3(0.5f, 1.2f, 0.06f), Brass.Shade(1.1f));
        }
        return mb.Build(Art.Hull);
    }

    /// <summary>Hull camera pods: (position, outward direction). Index = camera number - 1.</summary>
    public static readonly (Vector3 pos, Vector3 dir)[] CameraPods =
    {
        (new Vector3(0, 0.9f, -16.4f), Vector3.Forward),
        (new Vector3(-3.15f, 1.1f, -5.5f), Vector3.Left),
        (new Vector3(3.15f, 1.1f, -5.5f), Vector3.Right),
        (new Vector3(0, -3.05f, -8f), Vector3.Down),
        (new Vector3(0, 3.0f, 11.5f), Vector3.Back),
        (new Vector3(0, 7.45f, -2.2f), Vector3.Up),
    };
}
