using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Solitude.Core;
using Solitude.Systems;
using Solitude.UI;
using Line = Solitude.UI.TerminalScreen.Line;

namespace Solitude;

/// <summary>
/// THE SHIP'S LOG (Tom's idea): a green terminal on the galley wall that keeps a timestamped record of
/// everything that happens on the dive, every note found on the stations, the open goals, and the
/// boat's condition. Sit at it with E.
///
/// The BOAT page only REPORTS. Nothing can be switched or fixed from here: you still walk to the
/// breach with the welder, to the rack to swap a module (Tom: "I want the player to do things...
/// this world is a world I want to spend as much time in as possible").
///
/// J opens the JOURNAL: the same notes and goals on paper, anywhere you can walk (aboard or in a station).
/// </summary>
public partial class Main
{
    private readonly ShipLog _shipLog = new();
    private SubViewport _logVp, _journalVp;
    private TerminalScreen _logScreen, _journalScreen;
    private bool _journalOpen;

    /// <summary>Write a line in the ship's log (timestamped with the dive clock and depth).</summary>
    private void Log(string text)
    {
        if (_mode == Mode.Title) return;
        float depth = _diver != null && _diver.Active ? -_diver.GlobalPosition.Y : _sub.Depth;
        _shipLog.Add(text, depth);
    }

    /// <summary>
    /// The HUD messages worth keeping. The log takes them as they are (they're already written in the
    /// boat's voice), minus key hints. Anything else (prompts, "can't do that", tutorials) is not logged.
    /// </summary>
    private static readonly string[] LogWorthy =
    {
        "!! HULL BREACH", "Breach welded", "Patch welded", "Power cell installed", "The water finds its level",
        "BEACON ACQUIRED", "DOCKED:", "CLAMPS RELEASED", "MAP FRAGMENT", "DATA CORE",
        "The cell seats", "The lights are on", "The keycard reader turns green", "The panel gives", "RED KEYCARD",
        "The station keeps you", "You wake in your bunk", "The door is torn open", "The light through the slats",
        "FABRICATED", "FITTED", "REMOVED", "BLUEPRINT",
        "Something enormous", "Teeth.", "Something hit you", "MONITORS: motion", "It nudges you", "The whale",
        "Something answered the ping", "IT FOUND YOU", "It passes.", "It brushes past", "It's right under you",
        "It hit the boat", "FLOOD CIRCUIT TRIPPED.", "It hit you.", "It lost the light", "HYDROPHONE: something plated",
        "Outside.", "Airlock drained", "LOW AIR",
        "Recorded:", "Same signature", "IDENTIFIED", "It's very faint",
        "THE CRYSTAL EXPANSE", "THE HADAL DEEP", "THE PALE SHELF",
        "The Sounding has stopped", "The light turns toward you",
        "HYDROPHONE: a long, low rumble", "SILT.", "The silt is settling", "Something came back on the barbs",
        "HYDROPHONE: a distress pinger", "COMMS:", "RECORDER:", "WRECK:",
    };

    private void OnHudMessage(string m)
    {
        if (string.IsNullOrEmpty(m) || m.StartsWith("[debug]")) return;
        bool keep = false;
        foreach (var p in LogWorthy) if (m.StartsWith(p, StringComparison.Ordinal)) { keep = true; break; }
        if (!keep) return;
        bool alert = m.StartsWith("!!") || m.StartsWith("IT FOUND") || m.StartsWith("It hit") || m.StartsWith("LOW AIR") || m.StartsWith("Something hit");
        m = m.Replace("!!", "").Trim();
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\s*\[[A-Z0-9/ ]{1,6}\]", "");
        Log(alert ? "! " + m : m);
    }

    /// <summary>Built before the interior, which puts the log's picture on its wall screen.</summary>
    private void SetupTerminal()
    {
        _logVp = MakeTerminalViewport("ShipLogScreen");
        _logScreen = new TerminalScreen
        {
            Title = "SHIP'S LOG", Clock = () => $"DIVE {ShipLog.Stamp(_shipLog.Clock)}", NotesSource = NotesList,
            Tabs = new List<TerminalScreen.Tab>
            {
                new() { Name = "LOG", Lines = LogLines, StickToBottom = true },
                new() { Name = "NOTES", Notes = true },
                new() { Name = "GOALS", Lines = GoalLines },
                new() { Name = "BOAT", Lines = BoatLines },
            },
        };
        _logVp.AddChild(_logScreen);

        _journalVp = MakeTerminalViewport("JournalPage");
        _journalVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;   // only drawn while open
        _journalScreen = new TerminalScreen
        {
            Title = "JOURNAL", Paper = true, NotesSource = NotesList,
            Clock = () => $"{(_player != null && _player.InStation ? "ON A STATION" : "ABOARD")}  ·  {ShipLog.Stamp(_shipLog.Clock)}",
            Tabs = new List<TerminalScreen.Tab>
            {
                new() { Name = "NOTES", Notes = true },
                new() { Name = "GOALS", Lines = GoalLines },
            },
            Hint = "A/D page   W/S note   wheel scroll   J / ESC close",
        };
        _journalVp.AddChild(_journalScreen);
    }

    private SubViewport MakeTerminalViewport(string name)
    {
        var vp = new SubViewport
        {
            Name = name, Size = new Vector2I(TerminalScreen.W, TerminalScreen.H), Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false,
        };
        AddChild(vp);
        return vp;
    }

    // ------------------------------------------------------------------ using it

    private void EnterTerminal()
    {
        _mode = Mode.Terminal;
        _journalOpen = false;
        _player.InputEnabled = false;
        _hud.SetCrosshair(false);
        _hud.SetPrompt("");
        _hud.SetHelp("");
        _audio.PlayBlip(1250f, 0.03f, 0.4f);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _logScreen.Hint = "A/D or click: page   W/S scroll / pick note   wheel scroll   E / ESC leave";
        _logScreen.SetTab(0);
        MoveStationCam(_interior.GlobalTransform * _interior.TerminalView);
        // Let the camera settle on the screen, then the screen fills the view.
        GetTree().CreateTimer(0.45).Timeout += () => { if (_mode == Mode.Terminal && !_journalOpen) _hud.ShowTerminal(_logVp.GetTexture(), crt: true); };
    }

    private void OpenJournal()
    {
        _mode = Mode.Terminal;
        _journalOpen = true;
        _player.InputEnabled = false;
        _hud.SetCrosshair(false);
        _hud.SetPrompt("");
        _audio.PlayAt("pickup", 0f, 0.35f);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _journalVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _hud.ShowTerminal(_journalVp.GetTexture(), crt: false);
    }

    private void CloseTerminal()
    {
        _hud.ShowTerminal(null);
        _logScreen.Hint = "";
        _logScreen.SetTab(0);
        _journalVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        bool journal = _journalOpen;
        _journalOpen = false;
        if (journal)
        {
            // Back to wherever you were standing (aboard or in a station): nothing else changes.
            _mode = Mode.Walking;
            _player.InputEnabled = true;
            _hud.SetCrosshair(true);
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _audio.PlayAt("pickup", 0f, 0.25f);
            return;
        }
        _audio.PlayBlip(900f, 0.03f, 0.3f);
        EnterWalking();
    }

    private TerminalScreen ActiveTerminal => _journalOpen ? _journalScreen : _logScreen;

    private void UpdateTerminal()
    {
        var t = ActiveTerminal;
        if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact) ||
            (_journalOpen && Input.IsActionJustPressed(GameInput.Journal)))
        {
            CloseTerminal();
            return;
        }
        if (Input.IsActionJustPressed(GameInput.Left)) { t.NextTab(-1); _audio.PlayBlip(1400f, 0.015f, 0.25f); }
        if (Input.IsActionJustPressed(GameInput.Right)) { t.NextTab(1); _audio.PlayBlip(1400f, 0.015f, 0.25f); }
        if (Input.IsActionJustPressed(GameInput.Forward)) t.Step(-1);
        if (Input.IsActionJustPressed(GameInput.Back)) t.Step(1);
    }

    private void TerminalInput(InputEvent e)
    {
        var t = ActiveTerminal;
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) t.Wheel(-1);
            else if (mb.ButtonIndex == MouseButton.WheelDown) t.Wheel(1);
            else if (mb.ButtonIndex == MouseButton.Left) t.Click(_hud.TerminalPoint(mb.Position / 1f));
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key4)
            t.SetTab((int)(k.PhysicalKeycode - Key.Key1));
    }

    private void UpdateTerminalWorld(float dt)
    {
        if (_mode != Mode.Title && !_ended) _shipLog.Clock += dt;
        if (_mode != Mode.Title && _shipLog.Entries.Count == 0) Log("Systems up. The log is running.");
        // J: the journal, wherever you can walk.
        if (_mode == Mode.Walking && Input.IsActionJustPressed(GameInput.Journal) && !_player.Hidden) OpenJournal();
    }

    // ------------------------------------------------------------------ pages

    private List<(string, string, string)> NotesList() =>
        _shipLog.Notes.Select(n => (n.Title, $"{n.Where}  ·  found {ShipLog.Stamp(n.T)}", n.Body)).ToList();

    private List<Line> LogLines()
    {
        var l = new List<Line>();
        if (_shipLog.Entries.Count == 0) l.Add(new Line("No entries yet.", 2));
        foreach (var e in _shipLog.Entries)
            l.Add(new Line($"{ShipLog.Stamp(e.T)}  {e.Depth,4:0} m  {e.Text}", e.Text.StartsWith("!") ? 3 : 0));
        return l;
    }

    private static string Compass(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float brg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(d.X, -d.Z)), 360f);
        float dist = new Vector2(d.X, d.Z).Length();
        return $"bearing {Mathf.RoundToInt(brg) % 360:000}°, {(dist >= 1000f ? $"{dist / 1000f:0.0} km" : $"{dist:0} m")}";
    }

    private List<Line> GoalLines()
    {
        var l = new List<Line>();
        Vector3 here = _sub.GlobalPosition;

        l.Add(new Line("THE SOUNDING", 1));
        int cores = Mathf.Min(_coreSites.Count, CoresNeeded);
        if (_ended) l.Add(new Line("It has stopped counting. The ocean is very quiet. You may keep diving."));
        else if (!_originKnown)
        {
            l.Add(new Line($"[{(cores > 0 ? "~" : " ")}] Read the station data cores: {cores} of {CoresNeeded}."));
            l.Add(new Line("    Every station keeps its core memory somewhere deep inside.", 2));
        }
        else
        {
            l.Add(new Line($"[x] Data cores: {CoresNeeded} of {CoresNeeded}. The source is triangulated."));
            l.Add(new Line($"[ ] Go down the shaft: {Compass(here, _gen.Origin)}, bottom ~{-_gen.OriginFloor:0} m."));
            l.Add(new Line($"    Hull rated to {_sub.TestDepth:0} m, crush depth {_sub.CrushDepth:0} m.", _sub.CrushDepth < -_gen.OriginFloor ? 3 : 2));
        }

        l.AddRange(HannaGoalLines());   // a person comes before the inventory

        l.Add(new Line(""));
        l.Add(new Line("STATIONS", 1));
        var known = _stations.Directory.Generated.Where(s => s.Known).ToList();
        int visited = known.Count(s => _stations.States.TryGetValue(s.Id, out var st) && st.Visited);
        int powered = _stations.States.Values.Count(s => s.PowerRestored);
        l.Add(new Line($"Known {known.Count}   visited {visited}   power restored {powered}"));
        foreach (var s in known.Where(s => !(_stations.States.TryGetValue(s.Id, out var st) && st.Visited))
                               .OrderBy(s => s.Position.DistanceTo(here)).Take(3))
            l.Add(new Line($"[ ] {s.Name}: {Compass(here, s.Position)}"));
        if (known.Count == 0) l.Add(new Line("Listen for beacons. Ping. Watch the holo table.", 2));

        l.Add(new Line(""));
        l.Add(new Line("BLUEPRINTS", 1));
        bool any = false;
        foreach (var id in _modules.Known.OrderBy(i => (int)i))
        {
            if (_modules.Has(id)) continue;
            var def = Modules.Def(id);
            if (_systems.Hold.Count(def.Item) > 0) { l.Add(new Line($"[~] {def.Name}: built, waiting in the hold. Fit it at the rack.")); any = true; continue; }
            bool ready = _systems.Hold.Has(def.Cost);
            l.Add(new Line($"[ ] {def.Name}: {Modules.CostText(def)}{(ready ? "  << parts ready" : "")}", ready ? 0 : 2));
            any = true;
        }
        if (!any) l.Add(new Line("Nothing waiting. Station cores hold more.", 2));

        l.Add(new Line(""));
        l.Add(new Line("LAB", 1));
        if (_lab.Running is ItemId under) l.Add(new Line($"[~] {Items.Name(under)} under the scope: {_lab.Progress * 100f:0}%"));
        var waiting = new List<string>();
        foreach (var st in Lab.All) if (!_lab.Done.Contains(st.Item) && _systems.Hold.Count(st.Item) > 0) waiting.Add(Items.Name(st.Item));
        if (waiting.Count > 0) l.Add(new Line($"[ ] Waiting in the hold: {string.Join(", ", waiting)}"));
        l.Add(new Line($"Studied {_lab.Done.Count}. Field notes are with the NOTES.", 2));

        l.AddRange(WreckGoalLines());

        l.Add(new Line(""));
        l.Add(new Line("HYDROPHONE", 1));
        l.Add(new Line($"Library {_phone.Recordings.Count}/{Hydrophone.MaxRecordings}   identified {_phone.Identified.Count}"));
        return l;
    }

    private List<Line> BoatLines()
    {
        var l = new List<Line>();
        l.Add(new Line("CONDITION  (report only: repairs happen by hand)", 1));
        l.Add(new Line($"HULL      {Bar(_sub.Hull / 100f)}  {_sub.Hull,3:0}%", _sub.Hull < 40f ? 3 : 0));
        l.Add(new Line($"POWER     {Bar(_systems.Power / 100f)}  {_systems.Power,3:0}%{(_systems.Emergency ? "  EMERGENCY" : _systems.Thermal ? "  +THERMAL" : "")}", _systems.Power < 20f ? 3 : 0));
        l.Add(new Line($"FOOD      {Bar(_systems.Food / 100f)}  {_systems.Food,3:0}%", _systems.Food < 20f ? 3 : 0));
        l.Add(new Line($"DEPTH     {_sub.Depth,5:0} m   rated {_sub.TestDepth:0} m   crush {_sub.CrushDepth:0} m", _sub.Depth > _sub.TestDepth ? 3 : 0));
        l.Add(new Line($"HOLD      {_systems.Hold.Weight}/{_systems.Hold.Capacity}"));
        l.Add(new Line(""));
        l.Add(new Line("SYSTEMS", 1));
        l.Add(new Line($"FLOODS    {(_sub.LightsOn ? "ON" : _floodTrip > 0f ? $"TRIPPED, resetting {_floodTrip:0} s" : "off")}", _floodTrip > 0f ? 3 : 0));
        l.Add(new Line($"INTAKE    {(_systems.IntakeOn ? "RUNNING" : "off")}"));
        l.Add(new Line($"CURRENT   {CurrentHelmText().Replace("CURRENT", "").Trim()}", _silt > 0.3f ? 3 : 0));
        l.Add(new Line($"NOISE     {NoiseDb:0} dB{(_listener.Noise < Listener.Quiet ? "  (silent)" : "")}"));
        var rooms = _hull.BreachRooms().ToList();
        if (rooms.Count == 0) l.Add(new Line("BREACHES  none"));
        else l.Add(new Line($"BREACHES  {rooms.Count}: {string.Join(", ", rooms)}. Weld them (multi-tool).", 3));
        if (_hull.TotalFlood > 0.05f) l.Add(new Line($"WATER IN  {_hull.TotalFlood:0.0}  (the bilge pumps it out once the breach is sealed)", 3));
        l.Add(new Line(""));
        l.AddRange(WearLines());
        l.Add(new Line(""));
        l.Add(new Line($"MODULES  {_modules.Used}/{_modules.Bays} bays", 1));
        foreach (var id in _modules.Fitted) { var d = Modules.Def(id); l.Add(new Line($"  {d.Name}: {d.Effect}")); }
        return l;
    }

    private static string Bar(float f)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(f * 16f), 0, 16);
        return "[" + new string('#', n) + new string('.', 16 - n) + "]";
    }
}
