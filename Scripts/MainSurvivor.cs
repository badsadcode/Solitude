using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Solitude.Interior;
using Solitude.Stations;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// SOMEONE ALIVE OUT THERE. Hanna Kowal, a station engineer, still alive on one of the stations.
/// Her voice comes over the comms set (NoorVox, a designed voice: tools/voice/). The player never
/// speaks: they answer with the boat. Two pings means yes. Going to her station and using the
/// supply lock by its airlock means "here, take it". Not coming means not coming.
///
///   None     -> (some time into the dive, deep, after a first station) she calls. Ping twice.
///   Contact  -> she gives her station's beacon and asks for air: 2 crystals + a power cell.
///   Supplies -> delivered at the lock: thanks, and a station with a live core goes on the holo.
///   Helped   -> later she asks about her sister's boat (and puts it on the beacons).
///   Sister   -> later still: an ascent capsule, one more power cell.
///   Ascent   -> delivered: goodbye. Gone. Not delivered, for long enough: she fades. Lost.
/// Two pings any time after first contact gets a hello. Saved.
/// </summary>
public partial class Main
{
    private enum Hanna { None, Contact, Supplies, Helped, Sister, Ascent, Gone, Lost }
    private Hanna _hanna = Hanna.None;
    private float _hannaT = 900f;          // seconds of dive until the next thing she does
    private float _hannaNeglect;           // seconds since she asked for something
    private float _hannaWindow;            // contact: how long she listens for your two pings
    private int _hannaFade;                // 0..3 calls as the air runs out
    private long _hannaStation;
    private bool _hannaIntercom, _hannaGift, _hannaHaldane;
    private int _hannaAttempts;
    private float _helloCd;
    private readonly List<double> _pingTimes = new();
    private readonly List<string> _hannaTranscript = new();

    private const float HannaFade1 = 1800f, HannaFade2 = 2400f, HannaLast = 3000f;   // 30 / 40 / 50 minutes

    private void SetupHanna()
    {
        _sonar.Pinged += OnPingForHanna;
    }

    private StationSite HannaSite => _hannaStation != 0 ? _stations.Directory.ById(_hannaStation) : null;
    private bool HannaActive => _hanna is Hanna.Supplies or Hanna.Helped or Hanna.Sister or Hanna.Ascent;

    /// <summary>She can reach you aboard, in the suit, or in her own station (intercom), not inside another station.</summary>
    private bool HannaCanReach => !_player.InStation || (_station != null && _station.Site.Id == _hannaStation);

    private void HannaSay(string id)
    {
        var line = VoiceLines.Get(id);
        string caption = line?.Caption ?? id;
        if (!QueueVoice(id, $"HANNA: \"{caption}\"", 0.9f)) _hud.Message($"HANNA: \"{caption}\"", 6f);
        Log($"Hanna: \"{caption}\"");
        _hannaTranscript.Add(caption);
        _shipLog.UpsertNote("Hanna Kowal", string.Join("\n\n", _hannaTranscript), "COMMS · LIVE");
    }

    private void UpdateHanna(float dt)
    {
        if (_mode == Mode.Title || _ended) return;
        _helloCd -= dt;
        if (_hanna is Hanna.Gone or Hanna.Lost) return;

        bool deep = (_diver.Active ? -_diver.GlobalPosition.Y : _sub.Depth) > 200f;
        bool calm = !VoiceBusy && HannaCanReach && !_listener.InEncounter;

        switch (_hanna)
        {
            case Hanna.None:
            case Hanna.Contact when _hannaWindow <= 0f:
            {
                bool visitedAny = _stations.States.Values.Any(s => s.Visited);
                if (deep && visitedAny) _hannaT -= dt;
                if (_hannaT <= 0f && calm && !_player.InStation)
                {
                    HannaSay(_hannaAttempts == 0 ? "hanna_contact1" : "hanna_contact2");
                    _hannaAttempts++;
                    _hanna = Hanna.Contact;
                    _hannaWindow = 90f;
                    _hannaT = 360f + GD.Randf() * 240f;   // if you don't answer, she tries again later
                }
                break;
            }
            case Hanna.Contact:
                _hannaWindow -= dt;
                break;
            case Hanna.Supplies:
            case Hanna.Ascent:
                _hannaNeglect += dt;
                if (calm && !_player.InStation)
                {
                    float fade1 = _hanna == Hanna.Ascent ? HannaFade1 + 300f : HannaFade1;
                    if (_hannaFade == 0 && _hannaNeglect > fade1) { _hannaFade = 1; HannaSay("hanna_fading1"); }
                    else if (_hannaFade == 1 && _hannaNeglect > fade1 + 600f) { _hannaFade = 2; HannaSay("hanna_fading2"); }
                    else if (_hannaFade == 2 && _hannaNeglect > fade1 + 1200f) { _hannaFade = 3; HannaSay("hanna_last"); _hanna = Hanna.Lost; Autosave("hanna"); }
                }
                break;
            case Hanna.Helped:
                _hannaT -= dt;
                if (_hannaT <= 0f && calm)
                {
                    HannaSay("hanna_sister");
                    MarkHaldane();
                    _hanna = Hanna.Sister;
                    _hannaT = 600f;
                }
                break;
            case Hanna.Sister:
                _hannaT -= dt;
                if (_hannaT <= 0f && calm)
                {
                    HannaSay("hanna_ascent");
                    _hanna = Hanna.Ascent;
                    _hannaNeglect = 0f; _hannaFade = 0; _hannaIntercom = false;
                }
                break;
        }
    }

    private void OnPingForHanna()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _pingTimes.Add(now);
        _pingTimes.RemoveAll(t => now - t > 15.0);
        if (_pingTimes.Count < 2) return;

        if (_hanna == Hanna.Contact && _hannaWindow > 0f)
        {
            _pingTimes.Clear();
            _hannaWindow = 0f;
            GetTree().CreateTimer(2.0).Timeout += AnswerContact;
            return;
        }
        // Two pings any time after that: she says hello (not too often).
        if (HannaActive && _helloCd <= 0f && HannaCanReach && now - _pingTimes[^2] < 9.0)
        {
            _pingTimes.Clear();
            _helloCd = 300f;
            GetTree().CreateTimer(2.5).Timeout += () => { if (!VoiceBusy) HannaSay("hanna_hello"); };
        }
    }

    private void AnswerContact()
    {
        // Her station: one you haven't been inside, a reasonable trip away (not the start).
        Vector3 here = _sub.GlobalPosition;
        var starter = _stations.Directory.Starter;
        var sites = _stations.Directory.Near(here, 3200f).Where(s => s != starter).ToList();
        var pick = sites.Where(s => !(_stations.States.TryGetValue(s.Id, out var st) && st.Visited))
                        .OrderBy(s => Mathf.Abs(s.Position.DistanceTo(here) - 1600f)).FirstOrDefault()
                   ?? sites.OrderBy(s => s.Position.DistanceTo(here)).FirstOrDefault();
        if (pick == null) { _hannaT = 120f; _hanna = Hanna.None; return; }
        _hannaStation = pick.Id;
        pick.Known = true;
        _hanna = Hanna.Supplies;
        _hannaNeglect = 0f; _hannaFade = 0;
        HannaSay("hanna_reply1");
        HannaSay("hanna_reply2");
        Log($"Beacon received: {pick.Name}. {Compass(here, pick.Position)}.");
        Autosave("hanna");
    }

    /// <summary>The Haldane's last position on the beacons (if there's one within reach and it's still calling).</summary>
    private void MarkHaldane()
    {
        if (_wrecks == null) return;
        var h = _wrecks.Near(_sub.GlobalPosition, 9000f)
            .Where(w => w.Kind == WreckKind.SupplySub && w.Variant == 0 && !w.Wrong)
            .OrderBy(w => w.Position.DistanceTo(_sub.GlobalPosition)).FirstOrDefault();
        if (h != null && !Silenced(h)) { _wrecksHeard.Add(h.Id); Log($"Hanna's beacon: SUPPLY SUB HALDANE, last position. {Compass(_sub.GlobalPosition, h.Position)}."); }
    }

    /// <summary>A recorder was pulled: if it's the Haldane's and she asked, she hears about it.</summary>
    private void HannaHeardRecorder(WreckSite w)
    {
        if (_hannaHaldane || w.Kind != WreckKind.SupplySub || w.Variant != 0 || w.Wrong) return;
        if (_hanna is not (Hanna.Sister or Hanna.Ascent)) return;
        _hannaHaldane = true;
        GetTree().CreateTimer(14.0).Timeout += () => HannaSay("hanna_haldane");
        if (_hanna == Hanna.Sister) _hannaT = Mathf.Min(_hannaT, 120f);
    }

    // ------------------------------------------------------------------ her station: the supply lock

    /// <summary>Called when the player steps into a station: hers gets the supply lock and the intercom.</summary>
    private void HannaStationEntered()
    {
        if (_station == null || _station.Site.Id != _hannaStation || _hanna == Hanna.None) return;
        if (_station.GetNodeOrNull("SupplyLock") == null)
        {
            var pose = _station.AirlockPose;
            Vector3 at = pose * new Vector3(1.1f, 1.1f, -0.9f);
            var mb = new Solitude.Core.MeshBuilder();
            mb.Box(Vector3.Zero, new Vector3(0.7f, 0.6f, 0.35f), new Color(0.32f, 0.34f, 0.32f));
            mb.Box(new Vector3(0, 0, 0.18f), new Vector3(0.56f, 0.46f, 0.02f), new Color(0.2f, 0.21f, 0.2f));
            mb.Box(new Vector3(0.25f, 0.24f, 0.19f), new Vector3(0.05f, 0.05f, 0.02f), new Color(1f, 0.6f, 0.15f, 0.5f));   // lamp
            var lockNode = new MeshInstance3D { Name = "SupplyLock", Mesh = mb.Build(Solitude.Core.Art.Machine) };
            _station.AddChild(lockNode);
            lockNode.Transform = new Transform3D(pose.Basis, at);
            var it = Interactable.Create(_station, at, new Vector3(0.8f, 0.7f, 0.6f), "Supply lock (intercom)", UseSupplyLock);
            it.Transform = new Transform3D(pose.Basis, at);
        }
        if ((_hanna == Hanna.Supplies || _hanna == Hanna.Ascent) && !_hannaIntercom)
        {
            _hannaIntercom = true;
            GetTree().CreateTimer(2.5).Timeout += () => HannaSay("hanna_intercom");
        }
    }

    private void UseSupplyLock()
    {
        var hold = _systems.Hold;
        switch (_hanna)
        {
            case Hanna.Supplies:
            {
                var need = new[] { (ItemId.Crystal, 2), (ItemId.PowerCell, 1) };
                if (!hold.Has(need)) { _hud.Message("The supply lock is empty. She asked for two lattice crystals and a power cell.", 5f); return; }
                hold.Remove(ItemId.Crystal); hold.Remove(ItemId.Crystal); hold.Remove(ItemId.PowerCell);
                _audio.PlayAt("airlock", 0f, 0.7f);
                _hanna = Hanna.Helped;
                _hannaT = 480f;
                HannaSay("hanna_thanks");
                HannaSay("hanna_core");
                RevealLiveCore();
                Autosave("hanna");
                break;
            }
            case Hanna.Ascent:
                if (hold.Count(ItemId.PowerCell) < 1) { _hud.Message("The supply lock is empty. She needs one more power cell.", 5f); return; }
                hold.Remove(ItemId.PowerCell);
                _audio.PlayAt("airlock", 0f, 0.7f);
                _hanna = Hanna.Gone;
                HannaSay("hanna_goodbye");
                Log("Somewhere above the station, an ascent capsule lets go.");
                Autosave("hanna");
                break;
            case Hanna.Helped:
            case Hanna.Sister:
                if (!_hannaGift)
                {
                    _hannaGift = true;
                    hold.Add(ItemId.PatchKit); hold.Add(ItemId.PatchKit); hold.Add(ItemId.Medkit);
                    _hud.Message("Something in the lock: two patch kits and a medkit, and a note in marker pen. FOR BOAT.", 6f);
                    _audio.PlayAt("pickup", 0f, 0.6f);
                }
                else _hud.Message("The supply lock is empty. Behind the inner door, very quietly, a radio is playing.", 5f);
                break;
            case Hanna.Gone:
                _hud.Message("The supply lock is empty. The inner door is open now. The room behind it is empty, and very clean.", 6f);
                break;
            default:
                _hud.Message("The supply lock is empty. The intercom only hisses.", 5f);
                break;
        }
    }

    /// <summary>Her gift of information: a station whose core you haven't read goes on the beacons.</summary>
    private void RevealLiveCore()
    {
        Vector3 here = _sub.GlobalPosition;
        var site = _stations.Directory.Near(here, 6000f)
            .Where(s => s.Id != _hannaStation && s != _stations.Directory.Starter && !_coreSites.Contains(s.Id))
            .OrderBy(s => s.Known ? 1 : 0).ThenBy(s => s.Position.DistanceTo(here)).FirstOrDefault();
        if (site == null) return;
        site.Known = true;
        Log($"Hanna's beacon: {site.Name}, its core still running. {Compass(here, site.Position)}.");
    }

    // ------------------------------------------------------------------ journal

    private List<UI.TerminalScreen.Line> HannaGoalLines()
    {
        var l = new List<UI.TerminalScreen.Line>();
        if (_hanna == Hanna.None) return l;
        l.Add(new UI.TerminalScreen.Line(""));
        l.Add(new UI.TerminalScreen.Line("HANNA KOWAL", 1));
        var site = HannaSite;
        string where = site != null ? $"{site.Name}: {Compass(_sub.GlobalPosition, site.Position)}" : "";
        l.Add(_hanna switch
        {
            Hanna.Contact => new UI.TerminalScreen.Line("[ ] Someone on the comms. She asked you to ping twice."),
            Hanna.Supplies => new UI.TerminalScreen.Line($"[ ] Air for Hanna: 2 lattice crystals + 1 power cell, to the supply lock at {where}"),
            Hanna.Helped => new UI.TerminalScreen.Line($"[x] Hanna has air. {where}"),
            Hanna.Sister => new UI.TerminalScreen.Line(_hannaHaldane ? "[x] You found the Haldane for her." : "[ ] Hanna's sister was on the Haldane. If you ever get there."),
            Hanna.Ascent => new UI.TerminalScreen.Line($"[ ] One more power cell for her ascent capsule, to the supply lock at {where}"),
            Hanna.Gone => new UI.TerminalScreen.Line("[x] Hanna went up.", 2),
            _ => new UI.TerminalScreen.Line("[ ] The comms are quiet.", 2),
        });
        return l;
    }

    // ------------------------------------------------------------------ save

    private Dictionary<string, string> SaveHanna() => new()
    {
        ["state"] = _hanna.ToString(), ["t"] = _hannaT.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["neglect"] = _hannaNeglect.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["fade"] = _hannaFade.ToString(), ["station"] = _hannaStation.ToString(), ["attempts"] = _hannaAttempts.ToString(),
        ["gift"] = _hannaGift.ToString(), ["haldane"] = _hannaHaldane.ToString(),
        ["transcript"] = string.Join("\u001e", _hannaTranscript),
    };

    private void LoadHanna(Dictionary<string, string> d)
    {
        if (d == null || d.Count == 0) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (d.TryGetValue("state", out var s) && Enum.TryParse<Hanna>(s, out var h)) _hanna = h;
        if (_hanna == Hanna.Contact) { _hanna = Hanna.None; _hannaT = 120f; }   // a call in progress starts over
        if (d.TryGetValue("t", out var t)) float.TryParse(t, System.Globalization.NumberStyles.Float, inv, out _hannaT);
        if (d.TryGetValue("neglect", out var n)) float.TryParse(n, System.Globalization.NumberStyles.Float, inv, out _hannaNeglect);
        if (d.TryGetValue("fade", out var f)) int.TryParse(f, out _hannaFade);
        if (d.TryGetValue("station", out var st)) long.TryParse(st, out _hannaStation);
        if (d.TryGetValue("attempts", out var a)) int.TryParse(a, out _hannaAttempts);
        if (d.TryGetValue("gift", out var g)) bool.TryParse(g, out _hannaGift);
        if (d.TryGetValue("haldane", out var hd)) bool.TryParse(hd, out _hannaHaldane);
        _hannaTranscript.Clear();
        if (d.TryGetValue("transcript", out var tr) && tr.Length > 0) _hannaTranscript.AddRange(tr.Split('\u001e'));
        if (HannaSite is { } site) site.Known = true;
    }
}
