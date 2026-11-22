using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Solitude.Systems;
using Solitude.World;

namespace Solitude;

/// <summary>
/// WRECKS AND DISTRESS CALLS (see Systems/Wrecks.cs, World/WreckBuilder.cs). Wrecks stream in around
/// the boat; their salvage is ordinary seabed pickups for the diver (saved the same way). Many still
/// have a pinger calling: the hydrophone hears it from far off, the comms set sometimes picks up the
/// loop the crew left running, the helm and the holo table get its bearing. Pull the recorder and
/// its last entries go in the journal, and the pinger stops.
/// </summary>
public partial class Main
{
    private WreckDirectory _wrecks;
    private readonly Dictionary<long, (WreckSite site, WreckBuilder.Built built)> _wreckNodes = new();
    private readonly HashSet<long> _wrecksHeard = new(), _wrecksVisited = new();
    private float _wreckScanT, _wreckHearT, _wreckBlinkT, _loopReplayT = 60f;

    public const float PingerRange = 1600f;

    private void SetupWrecks()
    {
        var starter = _stations.Directory.Starter;
        _wrecks = new WreckDirectory(Seed, _gen, p =>
            _stations.Directory.Near(p, 350f).Any() ||
            new Vector2(p.X - _gen.Origin.X, p.Z - _gen.Origin.Z).Length() < 700f ||
            (starter != null && new Vector2(p.X - starter.Position.X, p.Z - starter.Position.Z).Length() < 300f));
    }

    private bool Silenced(WreckSite w) => !w.Pinger || (w.HasRecorder && _terrain.IsTaken(w.SalvageId(0)));

    private void UpdateWrecks(float dt)
    {
        if (_wrecks == null || _mode == Mode.Title) return;
        Vector3 f = FocusPoint();

        // --- stream bodies in and out
        _wreckScanT -= dt;
        if (_wreckScanT <= 0f)
        {
            _wreckScanT = 1f;
            foreach (var w in _wrecks.Near(f, 650f))
                if (!_wreckNodes.ContainsKey(w.Id)) SpawnWreck(w);
            foreach (var id in _wreckNodes.Keys.ToList())
            {
                var (w, b) = _wreckNodes[id];
                if (new Vector2(w.Position.X - f.X, w.Position.Z - f.Z).Length() < 900f) continue;
                long lo = w.SalvageId(0), hi = w.SalvageId(63);
                _terrain.RemoveExternal(pk => pk.Id >= lo && pk.Id <= hi);
                b.Root.QueueFree();
                _wreckNodes.Remove(id);
            }
        }

        // --- pinger lights: a red blink every couple of seconds while the pinger runs
        _wreckBlinkT += dt;
        float ph = Mathf.PosMod(_wreckBlinkT, 2.4f);
        float flash = ph < 0.18f ? 1f : 0f;
        foreach (var (w, b) in _wreckNodes.Values)
        {
            if (b.Pinger == null) continue;
            float e = Silenced(w) ? 0f : flash;
            b.Pinger.LightEnergy = 3f * e;
            ((ShaderMaterial)((MeshInstance3D)b.Pinger.GetNode("PingerLens")).MaterialOverride).SetShaderParameter("emission_energy", 4f * e);
        }

        // --- hearing them (from the boat or the suit), and arriving at them
        _wreckHearT -= dt;
        if (_wreckHearT > 0f || _ended) return;
        _wreckHearT = 2f;
        foreach (var w in _wrecks.Near(f, PingerRange))
        {
            float d = new Vector2(w.Position.X - f.X, w.Position.Z - f.Z).Length();
            if (!Silenced(w) && _wrecksHeard.Add(w.Id))
            {
                _hud.Message($"HYDROPHONE: a distress pinger. {Compass(f, w.Position)}.", 7f);
                _audio.PlayBlip(1800f, 0.05f, 0.35f);
                // A few seconds later the comms set catches the crew's loop: voiced if we have it (twice,
                // the way a loop does), otherwise as text.
                var site = w;
                GetTree().CreateTimer(4.5).Timeout += () =>
                {
                    if (Silenced(site)) return;
                    if (PlayWreckLoop(site, first: true)) PlayWreckLoop(site, first: false);
                    else if (WreckDirectory.Loop(site) is string loop) _hud.Message(loop, 9f);
                };
                _loopReplayT = 70f;
                break;   // one at a time; the next one is heard on the next scan
            }
            // Close to a call you haven't answered, the loop keeps coming round on the comms set.
            if (d < 500f && _wrecksHeard.Contains(w.Id) && !Silenced(w) && !_player.InStation)
            {
                _loopReplayT -= 2f;
                if (_loopReplayT <= 0f && !VoiceBusy) { _loopReplayT = 55f + GD.Randf() * 30f; PlayWreckLoop(w, first: false); }
            }
            if (d < 70f && _wrecksVisited.Add(w.Id))
                _hud.Message($"WRECK: {w.Name}. {(w.Kind == WreckKind.CargoString ? "Containers spilled along a snapped tether." : "Nobody answers the hull knocks.")}", 6f);
        }
    }

    private void SpawnWreck(WreckSite w)
    {
        var b = WreckBuilder.Build(w, _gen);
        _game.AddChild(b.Root);
        _wreckNodes[w.Id] = (w, b);

        if (w.HasRecorder) AddSalvage(w, b, 0, ItemId.Recorder, b.Recorder);
        var loot = WreckDirectory.Salvage(w);
        for (int i = 0; i < loot.Count && b.Salvage.Count > 0; i++)
            AddSalvage(w, b, i + 1, loot[i], b.Salvage[i % b.Salvage.Count] + new Vector3(0, 0.15f * (i / b.Salvage.Count), 0));
    }

    private void AddSalvage(WreckSite w, WreckBuilder.Built b, int slot, ItemId type, Vector3 at)
    {
        var node = WreckBuilder.SalvageNode(type == ItemId.Recorder);
        b.Root.AddChild(node);
        node.Position = at;
        node.Rotation = new Vector3(0, slot * 1.3f, 0);
        _terrain.AddExternal(new Solitude.World.TerrainStreamer.Pickup { Id = w.SalvageId(slot), Type = type, Position = at + Vector3.Up * 0.2f, Node = node });
    }

    /// <summary>The diver bagged something from a wreck: a recorder is read on the spot.</summary>
    private bool OnWreckSalvage(long pickupId, ItemId got)
    {
        if (got != ItemId.Recorder) return false;
        _diver.Bag.Remove(ItemId.Recorder);
        var w = _wrecks.ById((pickupId & ~(1L << 62)) >> 6);
        if (w == null) return true;
        var (title, body) = WreckDirectory.Recorder(w);
        _shipLog.AddNote(title, body, w.Name);
        _hud.Message($"RECORDER: {w.Name}. Its last entries are in your journal (J). {(w.Pinger ? "Its pinger goes quiet." : "")}", 7f);
        _audio.PlayBlip(600f, 0.2f, 0.4f);
        PlayRecorder(w);
        return true;
    }

    // ------------------------------------------------------------------ instruments

    /// <summary>Helm line: the nearest distress call still calling.</summary>
    private string WreckHelmText()
    {
        if (_wrecks == null) return "";
        WreckSite best = null; float bd = float.MaxValue;
        foreach (var w in _wrecks.Near(_sub.GlobalPosition, PingerRange * 1.5f))
        {
            if (!_wrecksHeard.Contains(w.Id) || Silenced(w)) continue;
            float d = w.Position.DistanceTo(_sub.GlobalPosition);
            if (d < bd) { bd = d; best = w; }
        }
        if (best == null) return "";
        string name = _wrecksVisited.Contains(best.Id) ? best.Name : "UNKNOWN";
        return $"\nPINGER   {name}  {Compass(_sub.GlobalPosition, best.Position).Replace("bearing ", "BRG ")}";
    }

    private IEnumerable<Vector3> HeardWreckBeacons()
    {
        if (_wrecks == null) yield break;
        foreach (var w in _wrecks.Near(_sub.GlobalPosition, 2500f))
            if (_wrecksHeard.Contains(w.Id) && !Silenced(w)) yield return w.Position;
    }

    private IEnumerable<Vector3> WreckStructures(Vector3 around, float radius)
    {
        if (_wrecks == null) yield break;
        foreach (var w in _wrecks.Near(around, radius))
            for (int i = 0; i < (w.Kind == WreckKind.SupplySub || w.Kind == WreckKind.CargoString ? 4 : 2); i++)
                yield return w.Position + new Basis(Vector3.Up, w.Yaw) * new Vector3(0, 2f, (i - 1.5f) * 5f);
    }

    /// <summary>The GOALS page: calls you've heard and haven't answered.</summary>
    private List<UI.TerminalScreen.Line> WreckGoalLines()
    {
        var l = new List<UI.TerminalScreen.Line>();
        if (_wrecks == null) return l;
        Vector3 here = _sub.GlobalPosition;
        var open = _wrecks.Near(here, 6000f).Where(w => _wrecksHeard.Contains(w.Id) && !Silenced(w)).OrderBy(w => w.Position.DistanceTo(here)).Take(4).ToList();
        if (open.Count == 0 && _wrecksHeard.Count == 0) return l;
        l.Add(new UI.TerminalScreen.Line(""));
        l.Add(new UI.TerminalScreen.Line("DISTRESS CALLS", 1));
        foreach (var w in open)
            l.Add(new UI.TerminalScreen.Line($"[ ] {(_wrecksVisited.Contains(w.Id) ? w.Name : "Unknown pinger")}: {Compass(here, w.Position)}"));
        if (open.Count == 0) l.Add(new UI.TerminalScreen.Line("Nothing calling that you haven't answered.", 2));
        return l;
    }
}
