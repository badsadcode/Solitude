using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Interior;
using Solitude.Systems;

namespace Solitude.Stations;

/// <summary>
/// Restoring a dead station's power (Tom: "Restore power"). NoorLevel marks power junctions on the
/// walls; older blockouts get one beside the core. In a dark station a junction takes a power cell
/// from the boat's hold and brings the station back: a clunk, a rising hum, the lights catching room
/// by room outward from the junction, the doors working, the screens glowing, the electrified
/// floors going live. And everything aboard hears it. Restored stations stay restored (saved).
/// </summary>
public partial class StationInterior
{
    private Node3D _lightRoot;
    private readonly List<(Vector3 pos, Color tint)> _glowSpots = new();
    private readonly List<(Interactable use, MeshInstance3D red, MeshInstance3D green, OmniLight3D work)> _junctions = new();
    private readonly Dictionary<OmniLight3D, float> _lightDelay = new();
    private float _powerOnT = -1f;

    /// <summary>A junction box on the wall (marker frame: origin on the wall, facing into the room).</summary>
    private void AddJunction(Transform3D at, Vector3 fp)
    {
        float w = Mathf.Max(0.5f, fp.X), d = Mathf.Max(0.2f, fp.Z), h = Mathf.Max(0.7f, fp.Y);
        var root = new Node3D { Transform = at };
        AddChild(root);
        var mb = new MeshBuilder();
        mb.Box(new Vector3(0, h / 2f, d / 2f), new Vector3(w, h, d), new Color(0.5f, 0.45f, 0.2f));
        mb.Box(new Vector3(0, h / 2f, d + 0.01f), new Vector3(w * 0.85f, h * 0.85f, 0.02f), new Color(0.3f, 0.3f, 0.28f));
        mb.Box(new Vector3(w * 0.25f, h * 0.35f, d + 0.06f), new Vector3(0.06f, 0.25f, 0.06f), new Color(0.15f, 0.15f, 0.15f)); // the lever
        mb.Box(new Vector3(0, -0.6f, 0.05f), new Vector3(0.08f, 1.2f, 0.08f), new Color(0.12f, 0.12f, 0.12f));                        // cable down to the deck
        root.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        MeshInstance3D Lamp(Color c)
        {
            var lm = new MeshBuilder();
            lm.Box(new Vector3(-w * 0.25f, h * 0.75f, d + 0.03f), new Vector3(0.07f, 0.07f, 0.04f), new Color(c.R, c.G, c.B, 0.5f));
            var mi = new MeshInstance3D { Mesh = lm.Build(Art.Machine) };
            root.AddChild(mi);
            return mi;
        }
        var red = Lamp(new Color(1f, 0.15f, 0.1f));
        var green = Lamp(new Color(0.3f, 1f, 0.4f));
        // A caged work lamp over the box: dark until the power comes back.
        var cage = new MeshBuilder();
        cage.Box(new Vector3(0, h + 0.35f, 0.18f), new Vector3(0.22f, 0.1f, 0.16f), new Color(0.2f, 0.2f, 0.2f));
        cage.Box(new Vector3(0, h + 0.28f, 0.18f), new Vector3(0.14f, 0.05f, 0.1f), new Color(0.85f, 0.8f, 0.7f));   // plain glass: the light itself does the glowing
        root.AddChild(new MeshInstance3D { Mesh = cage.Build(Art.Machine) });
        var work = new OmniLight3D { Position = new Vector3(0, h + 0.15f, 0.5f), LightColor = new Color(1f, 0.85f, 0.65f), OmniRange = 5f, OmniAttenuation = 1.3f, LightEnergy = 0f };
        root.AddChild(work);
        var use = Interactable.Create(root, new Vector3(0, h / 2f, d + 0.2f), new Vector3(w + 0.3f, h + 0.3f, 0.5f), "", null);
        use.OnUse = () => UseJunction(root.GlobalPosition + root.GlobalBasis * new Vector3(0, h / 2f, d));
        _junctions.Add((use, red, green, work));
        RefreshJunctions();
    }

    private void RefreshJunctions()
    {
        foreach (var (use, red, green, work) in _junctions)
        {
            red.Visible = !Powered;
            green.Visible = Powered;
            work.LightEnergy = Powered ? 0.9f : 0f;
            use.Prompt = Powered ? "Power junction: live" : "Restore power (uses a power cell)";
        }
    }

    /// <summary>Older blockouts have no junction markers: put one by the core if the station is dark.</summary>
    private void EnsureJunction(Vector3? core)
    {
        if (_junctions.Count > 0 || core is not Vector3 c) return;
        AddJunction(new Transform3D(Basis.Identity, c + new Vector3(-1.6f, 1.2f, -0.9f)), new Vector3(0.6f, 0.25f, 0.8f));   // marker convention: origin already at mounting height
    }

    /// <summary>Dev: `--station-dark` makes every station unpowered (to test restoring it).</summary>
    public static bool ForceDark;

    /// <summary>Dev / capture: use the first junction.</summary>
    public void DebugUseJunction() { if (_junctions.Count > 0) _junctions[0].use.OnUse?.Invoke(); }

    private void UseJunction(Vector3 globalAt)
    {
        if (Powered) { Sound?.Invoke("empty", globalAt); return; }
        if (Systems.Hold.Count(ItemId.PowerCell) == 0)
        {
            Message?.Invoke("The junction is dead. It needs a power cell, and there isn't one in the boat's hold.");
            Sound?.Invoke("deny", globalAt);
            return;
        }
        Systems.Hold.Remove(ItemId.PowerCell);
        RestorePower(globalAt);
    }

    private void RestorePower(Vector3 globalAt)
    {
        State.PowerRestored = true;
        Powered = true;
        Sound?.Invoke("powerup", globalAt);
        BuildLights();
        // The lights catch room by room, outward from the junction.
        Vector3 local = ToLocal(globalAt);
        _lightDelay.Clear();
        foreach (var (l, _, _) in _lights)
        {
            _lightDelay[l] = 1.2f + l.Position.DistanceTo(local) * 0.07f;
            l.LightEnergy = 0f;
        }
        _powerOnT = _t;
        foreach (var d in _bulkheads) d.PowerOn();
        AddGlows();
        RefreshJunctions();
        Message?.Invoke("The cell seats with a clunk. Somewhere deep in the station, something big starts to turn.");
        // ...and everything aboard heard that.
        GetTree().CreateTimer(3.5).Timeout += () =>
        {
            int awake = 0;
            foreach (var c in Crawlers) if (c.Current != Solitude.Creatures.Crawler.State.Dead) { c.Hear(globalAt, 200f); awake++; }
            Message?.Invoke(awake > 0 ? "The lights are on. Every light. You hear things moving toward the junction." : "The lights are on. Nothing answers. That's almost worse.");
        };
    }

    /// <summary>Live screens and tanks spill a little light (only with power).</summary>
    private void AddGlows()
    {
        if (!Powered) return;
        foreach (var (pos, tint) in _glowSpots)
            AddChild(new OmniLight3D { Position = pos, LightColor = tint, LightEnergy = 0.35f, OmniRange = 2.6f, OmniAttenuation = 1.6f });
        _glowSpots.Clear();
    }

    /// <summary>During the power-on ripple: hold each light dark until its turn, then let it stutter alive.</summary>
    private bool PowerOnRipple(OmniLight3D l, float e)
    {
        if (_powerOnT < 0f || !_lightDelay.TryGetValue(l, out float delay)) return false;
        float since = _t - _powerOnT - delay;
        if (since < 0f) { l.LightEnergy = 0f; return true; }
        if (since < 0.6f) { l.LightEnergy = _rng.NextDouble() < 0.5 ? e * 1.4f : 0f; return true; }
        if (_t - _powerOnT > 12f) _powerOnT = -1f;
        return false;
    }
}
