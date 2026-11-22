using System;
using System.Collections.Generic;
using Godot;
using Solitude.Interior;
using Solitude.Systems;
using Line = Solitude.UI.TerminalScreen.Line;

namespace Solitude;

/// <summary>
/// The boat as a body (see Systems/Wear.cs): what wears what, what a worn part sounds and feels
/// like, and the service points where you put it right by hand. Kept quiet in the reports: you're
/// meant to learn the boat by ear.
/// </summary>
public partial class Main
{
    private readonly Wear _wear = new();
    private readonly Dictionary<Part, (Interactable use, ShaderMaterial lamp, Vector3 at)> _service = new();
    private float _wearSoundT = 6f, _sealBreachT = 120f, _airHaze, _lampFlickerT;
    private float _labIntakeYield = 1f, _lampBase = -1f;
    private static bool _toldStaleAir;

    private void SetupWear()
    {
        // (interior-local position, facing yaw, housing size)
        void Point(Part p, Vector3 at, float yaw, Vector3 size)
        {
            var (use, lamp) = _interior.ServicePoint(at, yaw, size, Wear.Of(p).Name.ToUpperInvariant(), () => Service(p));
            _service[p] = (use, lamp, at);
        }
        float hw = Solitude.Interior.SubInterior.HalfW, stern = Solitude.Interior.SubInterior.Stern;
        Point(Part.IntakeFilter, new Vector3(1.45f, 1.85f, stern - 0.16f), Mathf.Pi, new Vector3(0.6f, 0.5f, 0.2f));   // above the engine block, on the aft bulkhead
        Point(Part.HullSeals, new Vector3(-hw + 0.12f, 0.95f, 8.6f), Mathf.Pi / 2f, new Vector3(0.45f, 0.4f, 0.16f));
        Point(Part.Bearings, new Vector3(0.52f, 0.85f, 10.4f), -Mathf.Pi / 2f, new Vector3(0.5f, 0.35f, 0.14f));
        Point(Part.Scrubber, new Vector3(hw - 0.13f, 1.65f, 8.75f), -Mathf.Pi / 2f, new Vector3(0.5f, 0.55f, 0.2f));
        Point(Part.Electronics, new Vector3(-hw + 0.12f, 1.15f, -0.45f), Mathf.Pi / 2f, new Vector3(0.45f, 0.5f, 0.16f));

        _wear.Worsened += OnPartWorsened;
        _sub.Impact += s => { if (_mode != Mode.Title && s > 0.9f) _wear.Use(Part.HullSeals, s * 2.5f); };
        _player.Hurt += dmg => { if (_diver.Active) _wear.Use(Part.Suit, dmg * 0.3f); };
    }

    private void UpdateWear(float dt)
    {
        if (_mode == Mode.Title || _ended) return;
        bool docked = _stations.DockedAt != null;
        bool storm = _silt > 0.4f;

        // --- what wears it
        if (!docked)
        {
            if (_systems.IntakeOn) _wear.Use(Part.IntakeFilter, (0.045f + (storm ? 0.15f : 0f)) * dt);
            else if (storm) _wear.Use(Part.IntakeFilter, 0.02f * dt);
            if (_sub.Depth > 400f) _wear.Use(Part.HullSeals, 0.006f * dt);
            if (_sub.Depth > _sub.TestDepth) _wear.Use(Part.HullSeals, 0.04f * dt);
            _wear.Use(Part.Bearings, Mathf.Pow(Mathf.Abs(_sub.EngineOutput), 1.5f) * 0.03f * dt);
            _wear.Use(Part.Scrubber, 0.022f * dt);
            _wear.Use(Part.Electronics, (0.008f + (storm ? 0.06f : 0f)) * dt);
        }
        if (_diver.Active) _wear.Use(Part.Suit, 0.05f * dt);

        // --- what a worn part does
        var filter = _wear.StateOf(Part.IntakeFilter);
        _systems.IntakeYield = _labIntakeYield * (filter == Wear.State.Good ? 1f : filter == Wear.State.Worn ? 0.5f : 0f);

        var bearings = _wear.StateOf(Part.Bearings);
        _sub.WearLimit = bearings == Wear.State.Failing ? 0.6f : 1f;

        var air = _wear.StateOf(Part.Scrubber);
        bool aboard = !_diver.Active && !_player.InStation;
        float hazeTarget = !aboard ? 0f : air == Wear.State.Failing ? 0.35f : air == Wear.State.Worn ? 0.12f : 0f;
        _airHaze = Mathf.MoveToward(_airHaze, hazeTarget, dt * 0.05f);
        if (aboard && air == Wear.State.Failing) _player.Damage(0.2f * dt);
        if (aboard && air != Wear.State.Good && !_toldStaleAir) { _toldStaleAir = true; _hud.Message("The air tastes of pennies. There's a dull ache behind your eyes.", 6f); }

        var bus = _wear.StateOf(Part.Electronics);
        float busMurk = bus == Wear.State.Failing ? 0.6f : bus == Wear.State.Worn ? 0.25f * (0.5f + 0.5f * Mathf.Sin(_shipLog.Clock * 1.7f)) : 0f;
        _rig.Murk = Mathf.Max(_rig.Murk, busMurk);
        if (bus == Wear.State.Failing) _sonar.Clutter = Mathf.Max(_sonar.Clutter, 0.3f);

        var suit = _wear.StateOf(Part.Suit);
        _diver.AirFactor = suit == Wear.State.Failing ? 1.5f : suit == Wear.State.Worn ? 1.25f : 1f;
        // A failing suit's lamp cuts out for a moment now and then.
        if (_lampBase < 0f) _lampBase = _diver.Lamp.LightEnergy;
        if (_diver.Active && suit == Wear.State.Failing)
        {
            _lampFlickerT -= dt;
            if (_lampFlickerT <= 0f) _lampFlickerT = 3f + GD.Randf() * 6f;
            _diver.Lamp.LightEnergy = _lampFlickerT > 2.8f ? _lampBase * 0.08f : _lampBase;
        }
        else _diver.Lamp.LightEnergy = _lampBase;

        // Seals: a failing seam lets go eventually.
        if (_wear.StateOf(Part.HullSeals) == Wear.State.Failing && !docked)
        {
            _sealBreachT -= dt;
            if (_sealBreachT <= 0f) { _sealBreachT = 150f + GD.Randf() * 120f; _hull.OpenBreach(3, 0.3f); }
        }

        WearSounds(dt, aboard);
        UpdateServicePoints();
    }

    /// <summary>The sounds that tell you something's going: you learn your boat by ear.</summary>
    private void WearSounds(float dt, bool aboard)
    {
        if (!aboard) return;
        _wearSoundT -= dt;
        if (_wearSoundT > 0f) return;
        _wearSoundT = 5f + GD.Randf() * 9f;
        var worn = new List<Part>();
        foreach (var d in Wear.All) if (d.Part != Part.Suit && _wear.StateOf(d.Part) != Wear.State.Good) worn.Add(d.Part);
        if (worn.Count == 0) return;
        var p = worn[(int)(GD.Randi() % (uint)worn.Count)];
        bool failing = _wear.StateOf(p) == Wear.State.Failing;
        Vector3 at = _interior.GlobalTransform * _service[p].at;
        switch (p)
        {
            case Part.Bearings when Mathf.Abs(_sub.EngineOutput) > 0.15f:
                PlaySpatial("knock", at); if (failing) GetTree().CreateTimer(0.35).Timeout += () => PlaySpatial("knock", at);
                break;
            case Part.HullSeals: PlaySpatial("drip", at); GetTree().CreateTimer(0.6).Timeout += () => PlaySpatial("drip", at); break;
            case Part.IntakeFilter when _systems.IntakeOn: PlaySpatial("steam", at); break;
            case Part.Scrubber: PlaySpatial(failing ? "deny" : "steam", at); break;
            case Part.Electronics: _audio.PlayBlip(failing ? 140f : 9000f, 0.08f, 0.12f); break;
        }
    }

    /// <summary>Extra noise for the Listener from a knocking drive.</summary>
    private float WearNoise => _wear.StateOf(Part.Bearings) switch { Wear.State.Failing => 0.12f, Wear.State.Worn => 0.05f, _ => 0f } * Mathf.Abs(_sub.EngineOutput);

    private void UpdateServicePoints()
    {
        foreach (var (p, (use, lamp, _)) in _service)
        {
            var st = _wear.StateOf(p);
            var def = Wear.Of(p);
            Color c = st switch { Wear.State.Good => new Color(0.3f, 1f, 0.4f), Wear.State.Worn => new Color(1f, 0.55f, 0.08f), _ => new Color(1f, 0.1f, 0.05f) };
            float blink = st == Wear.State.Failing && Mathf.PosMod(_shipLog.Clock, 1f) < 0.5f ? 0.2f : 1f;
            lamp.SetShaderParameter("emission_color", c);
            lamp.SetShaderParameter("emission_energy", 1.4f * blink);
            string how = st == Wear.State.Good ? "fine for now" : Wear.Word(st).ToLowerInvariant();
            use.Prompt = _wear.Condition(p) > 90f ? $"{def.Name}: sound" : $"{def.FixVerb}  ({how}; needs {FixText(def)})";
        }
    }

    private static string FixText(Wear.Def d)
    {
        var parts = new List<string>();
        foreach (var (id, n) in d.Fix) if (n > 0) parts.Add($"{n}x {Items.Name(id).ToLowerInvariant()}");
        return parts.Count == 0 ? "only your hands" : string.Join(" + ", parts);
    }

    private void Service(Part p)
    {
        var def = Wear.Of(p);
        if (_wear.Condition(p) > 90f) { _hud.Message($"{def.Name}: nothing to do. It sounds fine.", 3f); return; }
        var cost = new List<(ItemId, int)>();
        foreach (var (id, n) in def.Fix) if (n > 0) cost.Add((id, n));
        if (!_systems.Hold.Has(cost)) { _hud.Message($"{def.FixVerb}: you need {FixText(def)}.", 4f); _audio.PlayAt("deny", 0f, 0.5f); return; }
        foreach (var (id, n) in cost) for (int i = 0; i < n; i++) _systems.Hold.Remove(id);
        _wear.Restore(p);
        _audio.PlayAt(p is Part.HullSeals or Part.Electronics ? "weld" : "reload", 0f, 0.6f);
        _hud.Message(p switch
        {
            Part.IntakeFilter => "You pull the filter, knock a fist of grey silt out of it, and slide it back. The intake breathes again.",
            Part.HullSeals => "Sealant into every seam you can reach. The dripping stops.",
            Part.Bearings => "Plankton rendered into something like grease, worked into the bearings. The knock is gone.",
            Part.Scrubber => "A crystal-lattice cartridge into the scrubber. The air stops tasting of pennies.",
            Part.Electronics => "New board in the sensor bus. The monitors steady.",
            _ => "Visor polished, valve re-seated, seals smeared with sealant. The suit is snug again.",
        }, 5f);
        Log($"Maintenance: {def.Name.ToLowerInvariant()} serviced.");
    }

    private void OnPartWorsened(Part p, Wear.State s)
    {
        string where = Wear.Of(p).Room;
        string line = (p, s) switch
        {
            (Part.IntakeFilter, Wear.State.Worn) => "the intake sounds laboured.",
            (Part.IntakeFilter, _) => "the intake has choked. It's pulling nothing.",
            (Part.HullSeals, Wear.State.Worn) => "a seam has started to weep.",
            (Part.HullSeals, _) => "water is finding the seams.",
            (Part.Bearings, Wear.State.Worn) => "a new knock in the drive.",
            (Part.Bearings, _) => "the drive is knocking hard. It won't take full power.",
            (Part.Scrubber, Wear.State.Worn) => "the air is going stale.",
            (Part.Scrubber, _) => "the air is bad.",
            (Part.Electronics, Wear.State.Worn) => "the monitors have started to flicker.",
            (Part.Electronics, _) => "the sensor bus is failing.",
            (Part.Suit, Wear.State.Worn) => "the suit's valve hisses a little at depth.",
            _ => "the suit is leaking air.",
        };
        Log($"{(s == Wear.State.Failing ? "! " : "")}{where}: {line}");
        if (s == Wear.State.Failing) _hud.Message($"{where}: {line}", 5f);
    }

    /// <summary>The BOAT page's upkeep section: words, not numbers.</summary>
    private List<Line> WearLines()
    {
        var l = new List<Line> { new(""), new("UPKEEP  (by ear)", 1) };
        foreach (var d in Wear.All)
        {
            var st = _wear.StateOf(d.Part);
            l.Add(new Line($"{d.Name,-15} {Wear.Word(st),-8} {d.Room}", st == Wear.State.Good ? 0 : 3));
        }
        return l;
    }
}
