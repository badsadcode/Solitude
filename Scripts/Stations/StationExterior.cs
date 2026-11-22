using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Stations;

/// <summary>
/// The outside of a station: pressure modules on legs, a mast with a strobing beacon, portholes
/// (lit or dead depending on power), a berth gantry along the +X side, and an approach lane of
/// marker lights so docking reads like landing on a lit runway. Local space: the main module runs
/// along Z; the boat docks alongside at DockLocal, bow toward -Z, entering from +Z.
/// </summary>
public partial class StationExterior : Node3D
{
    public static readonly Transform3D DockLocal = new(Basis.Identity, new Vector3(17f, 10f, -2f));
    public const float LaneLength = 80f;

    public StationSite Site;
    public bool Powered;

    private readonly List<OmniLight3D> _laneLights = new();
    private readonly List<MeshInstance3D> _laneBulbs = new();
    private OmniLight3D _beacon;
    private float _t;
    private ShaderMaterial _laneMat;

    private static readonly Color Shell = new(0.42f, 0.44f, 0.4f);
    private static readonly Color Rust = new(0.45f, 0.25f, 0.15f);
    private static readonly Color Dark = new(0.13f, 0.14f, 0.14f);

    public override void _Ready()
    {
        var rng = new Random(Site.Seed);
        var mb = new MeshBuilder();
        var body = new StaticBody3D { CollisionLayer = Layers.World, CollisionMask = 0 };
        AddChild(body);
        void Solid(Vector3 c, Vector3 size) => body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = c });

        // Legs down into the sediment
        foreach (var (x, z) in new[] { (-4f, -11f), (4f, -11f), (-4f, 11f), (4f, 11f), (-9f, 0f), (9f, 0f) })
        {
            mb.Cylinder(new Vector3(x, -14f, z), 0.7f, 0.5f, 20f, 6, Dark.Shade(1.3f), caps: false);
            mb.Cylinder(new Vector3(x, -0.2f, z), 1.4f, 1.0f, 0.6f, 6, Rust, caps: true); // foot
            Solid(new Vector3(x, -4f, z), new Vector3(1.2f, 20f, 1.2f));
        }
        // Platform
        mb.Box(new Vector3(0, 5.5f, 0), new Vector3(20f, 0.8f, 28f), Dark.Shade(1.4f));
        Solid(new Vector3(0, 5.5f, 0), new Vector3(20f, 0.8f, 28f));

        // Main module (cylinder along Z) and a cross module along X
        mb.Xform = new Transform3D(Basis.Identity, new Vector3(0, 10.5f, 0));
        mb.LatheZ(new List<Vector2> { new(-14f, 0f), new(-13.6f, 3.2f), new(-12.5f, 4.4f), new(12.5f, 4.4f), new(13.6f, 3.2f), new(14f, 0f) }, 12, Shell, 1f,
            (r, i) => (r % 2 == 0 ? Shell : Shell.Shade(0.9f)).Lerp(Rust, i % 5 == 0 ? 0.5f : 0f));
        mb.Xform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-6f, 9.5f, 6f));
        mb.LatheZ(new List<Vector2> { new(-8f, 0f), new(-7.6f, 2.6f), new(-6.8f, 3.4f), new(6.8f, 3.4f), new(7.6f, 2.6f), new(8f, 0f) }, 10, Shell.Shade(0.95f));
        mb.Xform = Transform3D.Identity;
        Solid(new Vector3(0, 10.5f, 0), new Vector3(8.6f, 8.6f, 27f));
        Solid(new Vector3(-6f, 9.5f, 6f), new Vector3(16f, 6.6f, 6.6f));

        // Dome observatory and mast with the beacon
        mb.Rock(new Vector3(0, 14.6f, -6f), new Vector3(3.2f, 2.6f, 3.2f), Shell.Shade(1.1f), 7, 0.02f);
        mb.Cylinder(new Vector3(2f, 14.5f, 6f), 0.25f, 0.15f, 12f, 5, Dark);
        mb.Box(new Vector3(2f, 22f, 6f), new Vector3(2.4f, 0.12f, 0.12f), Dark);
        mb.Rock(new Vector3(2f, 26.7f, 6f), new Vector3(0.35f, 0.35f, 0.35f), new Color(1f, 0.15f, 0.1f, 0.4f), 3, 0.05f);
        _beacon = new OmniLight3D { Position = new Vector3(2f, 26.7f, 6f), LightColor = new Color(1f, 0.15f, 0.1f), OmniRange = 35f, LightEnergy = 0f, LightVolumetricFogEnergy = 3f };
        AddChild(_beacon);

        // Portholes: warm and alive, or dark glass
        Color port = Powered ? new Color(1f, 0.75f, 0.4f, 0.4f) : new Color(0.05f, 0.06f, 0.07f);
        for (int i = 0; i < 9; i++)
        {
            float z = -10f + i * 2.5f;
            foreach (int s in new[] { -1, 1 })
                mb.Box(new Vector3(s * 4.45f, 11f, z), new Vector3(0.1f, 0.6f, 0.6f), (rng.NextDouble() < 0.25 && Powered) ? new Color(0.05f, 0.06f, 0.07f) : port);
        }
        if (Powered)
            for (int i = 0; i < 3; i++)
                AddChild(new OmniLight3D { Position = new Vector3(i % 2 == 0 ? 5.5f : -5.5f, 11f, -8f + i * 8f), LightColor = new Color(1f, 0.75f, 0.45f), LightEnergy = 1.2f, OmniRange = 9f });

        // Berth gantry on the +X side, with clamp arms reaching for the boat
        mb.Box(new Vector3(10.5f, 10f, -2f), new Vector3(3f, 1.2f, 34f), Dark.Shade(1.2f));
        Solid(new Vector3(10.5f, 10f, -2f), new Vector3(3f, 1.2f, 34f));
        for (int i = 0; i < 4; i++)
        {
            float z = -14f + i * 8f;
            mb.Box(new Vector3(9.5f, 7f, z), new Vector3(0.6f, 6f, 0.6f), Dark);
            mb.Box(new Vector3(12.6f, 10f, z), new Vector3(1.4f, 0.5f, 0.5f), new Color(0.9f, 0.7f, 0.1f)); // clamp arm
        }
        // Boarding collar where the dive hatch meets the station
        mb.Cylinder(new Vector3(13.2f, 9.6f, 7f), 1.1f, 1.1f, 1.2f, 8, Rust);
        mb.Box(new Vector3(11.8f, 10.2f, 7f), new Vector3(2.4f, 1.4f, 1.4f), Shell.Shade(0.8f));

        // Exterior floods under the platform (only with power)
        if (Powered)
            foreach (var z in new[] { -10f, 10f })
                AddChild(new SpotLight3D { Position = new Vector3(0, 5f, z), Rotation = new Vector3(-Mathf.Pi / 2f, 0, 0), SpotRange = 25f, SpotAngle = 55f, LightEnergy = 2f, LightVolumetricFogEnergy = 1.5f });

        AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Hull) });

        // Approach lane: pairs of marker posts leading into the berth from +Z.
        _laneMat = (ShaderMaterial)Art.Hull.Duplicate();
        _laneMat.SetShaderParameter("emission_energy", 4f);
        _laneMat.SetShaderParameter("emission_color", new Color(0.3f, 1f, 0.4f));
        var bulb = new MeshBuilder();
        bulb.Rock(Vector3.Zero, new Vector3(0.25f, 0.25f, 0.25f), new Color(1f, 1f, 1f, 0.4f), 1, 0.05f);
        var bulbMesh = bulb.Build(_laneMat);
        var post = new MeshBuilder();
        post.Cylinder(Vector3.Zero, 0.12f, 0.1f, 1f, 5, Dark);
        var postMesh = post.Build(Art.Hull);
        for (int i = 0; i < 6; i++)
        {
            float z = DockLocal.Origin.Z + 22f + i * 11f;
            foreach (float x in new[] { DockLocal.Origin.X - 7f, DockLocal.Origin.X + 7f })
            {
                float y = DockLocal.Origin.Y - 3f;
                AddChild(new MeshInstance3D { Mesh = postMesh, Position = new Vector3(x, -12f, z), Scale = new Vector3(1, y + 12f, 1) });
                var b = new MeshInstance3D { Mesh = bulbMesh, Position = new Vector3(x, y, z) };
                AddChild(b);
                _laneBulbs.Add(b);
                if (i % 2 == 0)
                {
                    var l = new OmniLight3D { Position = new Vector3(x, y + 0.4f, z), LightColor = new Color(0.3f, 1f, 0.4f), LightEnergy = 0.8f, OmniRange = 8f };
                    AddChild(l);
                    _laneLights.Add(l);
                }
            }
        }
    }

    /// <summary>Lane colour: green = aligned, amber = off, red = too fast. Driven by the docking computer.</summary>
    public void SetLaneState(Color c)
    {
        foreach (var l in _laneLights) l.LightColor = c;
        _laneMat.SetShaderParameter("emission_color", c);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        float phase = Mathf.PosMod(_t, 3.2f);
        _beacon.LightEnergy = phase < 0.12f ? 6f : phase < 0.3f ? 1f : 0f;
        // Chasing lane lights, running toward the berth.
        for (int i = 0; i < _laneBulbs.Count; i++)
        {
            int row = 5 - i / 2;
            bool on = Mathf.PosMod(_t * 3f - row, 6f) < 1.2f;
            _laneBulbs[i].Scale = Vector3.One * (on ? 1.6f : 1f);
        }
    }
}
