using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Sonar;
using Solitude.Stations;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// The hydrophone console's glue (see Systems/Hydrophone.cs): gathers every sound source around the
/// boat (creatures, vents, stations, the Listener, the Sounding from the Origin, the odd geological
/// crack), feeds the waterfall, steers the listening beam, plays the beam in the headphones, and
/// keeps the library of recordings.
/// </summary>
public partial class Main
{
    private readonly Hydrophone _phone = new();
    private readonly List<HydroSource> _hydroSources = new();
    private SubViewport _hydroScope;
    private HydrophoneDisplay _hydroDisplay;
    private float _beam, _recordT = -1f, _geoT = 30f, _geoLife, _listenerBrg;
    private HydroSource _geo;
    private readonly Random _hydroRng = new();
    private const float RecordSeconds = 4f;

    private void SetupHydrophone()
    {
        _hydroScope = new SubViewport
        {
            Name = "HydroScope", Size = new Vector2I(256, 256), Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_hydroScope);
        _hydroDisplay = new HydrophoneDisplay { Phone = _phone, Size = new Vector2(256, 256) };
        _hydroScope.AddChild(_hydroDisplay);
        _interior.SetHydrophoneTexture(_hydroScope.GetTexture());
        _listenerBrg = (float)_hydroRng.NextDouble() * 360f - 180f;
    }

    // ------------------------------------------------------------------ the world, as sound

    /// <summary>Bearing relative to the bow, degrees, + = starboard.</summary>
    private float RelBearing(Vector3 world)
    {
        Vector3 local = _sub.GlobalBasis.Orthonormalized().Inverse() * (world - _sub.GlobalPosition);
        return Mathf.RadToDeg(Mathf.Atan2(local.X, -local.Z));
    }

    private void AddSource(SoundClass c, Vector3 at, float loud, float range, float spread = 5f)
    {
        float d = at.DistanceTo(_sub.GlobalPosition);
        if (d > range) return;
        float level = loud * Mathf.Pow(1f - d / range, 1.6f);
        if (level < 0.01f) return;
        _hydroSources.Add(new HydroSource { Class = c, Bearing = RelBearing(at), Level = level, Spread = spread, Distance = d });
    }

    private void GatherSounds(float dt)
    {
        _hydroSources.Clear();
        Vector3 p = _sub.GlobalPosition;
        if (_ravager != null && _ravager.IsInsideTree() && _ravager.Visible) AddSource(SoundClass.Ravager, _ravager.GlobalPosition, 0.9f, 900f);
        foreach (var r in _creatures.Rays) if (r.IsInsideTree()) AddSource(SoundClass.Ray, r.GlobalPosition, 0.5f, 350f);
        if (_creatures.Wyrm is { } w && w.IsInsideTree()) AddSource(SoundClass.Wyrm, w.GlobalPosition, 1f, 1400f, 7f);
        if (_creatures.Whale is { } wh && wh.IsInsideTree()) AddSource(SoundClass.Whale, wh.GlobalPosition, 1f, 2000f, 9f);
        if (SiltRumble(out var siltAt, out var siltLvl)) AddSource(SoundClass.Geology, siltAt, siltLvl, 3000f, 30f);
        if (_gloam != null && _gloam.IsInsideTree()) AddSource(SoundClass.Gloam, _gloam.Head, _gloam.Current == Creatures.Gloam.State.Prowl ? 0.5f : 0.85f, 700f, 5f);
        int vents = 0;
        foreach (var v in _terrain.VentsNear(p, 500f)) { if (++vents > 6) break; AddSource(SoundClass.Vent, v, 0.45f, 500f); }
        var heard = new HashSet<long>();
        foreach (var s in _stations.Directory.Near(p, 1500f))
            if (heard.Add(s.Id)) AddSource(SoundClass.Station, s.Position, StationInterior.IsPowered(s) || _stations.States.TryGetValue(s.Id, out var st) && st.PowerRestored ? 0.55f : 0.2f, 1500f, 4f);

        // The Listener: a huge, smeared, wandering presence once it's paying attention.
        _listenerBrg = Hydrophone.Wrap(_listenerBrg + ((float)_hydroRng.NextDouble() - 0.5f) * 20f * dt);
        if (_listener.Attention > 25f)
            _hydroSources.Add(new HydroSource { Class = SoundClass.Listener, Bearing = _listenerBrg, Level = (_listener.Attention - 25f) / 75f * 0.85f, Spread = 40f, Distance = 9999f });

        // The Sounding: from the Origin, always. Faint at first; louder as the cores are found.
        _hydroSources.Add(new HydroSource { Class = SoundClass.Sounding, Bearing = RelBearing(_gen.Origin), Level = 0.09f + 0.3f * StoryUnease, Spread = 4f, Distance = _gen.Origin.DistanceTo(p) });

        // Now and then the rock itself cracks somewhere: false alarms.
        _geoT -= dt;
        if (_geoT <= 0f)
        {
            _geoT = 25f + (float)_hydroRng.NextDouble() * 45f;
            _geoLife = 1.5f + (float)_hydroRng.NextDouble() * 2f;
            _geo = new HydroSource { Class = SoundClass.Geology, Bearing = (float)_hydroRng.NextDouble() * 360f - 180f, Level = 0.4f + (float)_hydroRng.NextDouble() * 0.5f, Spread = 5f, Distance = 800f };
        }
        if (_geoLife > 0f) { _geoLife -= dt; _hydroSources.Add(_geo); }
    }

    /// <summary>The source that dominates the beam right now (or none), and the beam's total level.</summary>
    private (HydroSource? src, float level) InBeam()
    {
        HydroSource? best = null;
        float bestV = 0f, total = 0f;
        foreach (var s in _hydroSources)
        {
            float v = s.Level * _phone.BandGain(Hydrophone.Of(s.Class)) * Hydrophone.BeamWeight(_beam, s.Bearing, Mathf.Max(6f, s.Spread));
            total += v;
            if (v > bestV) { bestV = v; best = s; }
        }
        // Your own noise drowns out anything quieter than about half of it.
        float masking = Mathf.Max(0.025f, _listener.Noise * 0.5f);
        return (bestV > masking ? best : null, total);
    }

    /// <summary>Runs every frame (the waterfall keeps scrolling whether anyone is watching or not).</summary>
    private void UpdateHydrophoneWorld(float dt)
    {
        if (_mode == Mode.Title) return;
        GatherSounds(dt);
        _phone.Update(dt, _hydroSources, _listener.Noise);
        bool listening = _mode == Mode.Hydrophone;
        _audio.Hydro = listening;
        if (!listening) { _recordT = -1f; _hydroDisplay.RecordProgress = -1f; }
        UpdateHydroReadout(listening);
    }

    private void UpdateHydroReadout(bool listening)
    {
        var (src, level) = InBeam();
        float db = 40f + Mathf.Clamp(level, 0f, 1.2f) * 55f;
        string side = _beam > 0.5f ? "S" : _beam < -0.5f ? "P" : "";
        _hydroDisplay.Beam = _beam;
        _hydroDisplay.SelfNoisy = _listener.Noise > 0.3f;
        _hydroDisplay.Line1 = $"BEAM {side}{Mathf.Abs(Mathf.RoundToInt(_beam)):000}°   {db:0} dB";
        if (src is HydroSource s)
        {
            _hydroDisplay.Line2 = Hydrophone.Describe(s.Class);
            var rec = _phone.MatchOf(s.Class);
            _hydroDisplay.Line3 = _phone.Identified.Contains(s.Class) ? $"ID: {Hydrophone.Name(s.Class)}{(rec != null ? $" (REC {rec.No:00})" : "")}"
                                : rec != null ? $"MATCH REC {rec.No:00}" : "NO MATCH IN LIBRARY";
        }
        else
        {
            _hydroDisplay.Line2 = _listener.Noise > 0.3f ? "OWN NOISE: NOTHING CAN BE HEARD" : "SEA NOISE";
            _hydroDisplay.Line3 = "";
        }
        _hydroDisplay.Line4 = $"LIBRARY {_phone.Recordings.Count}/{Hydrophone.MaxRecordings}   [R] REC  [Q] BAND  [A/D] STEER";

        // Headphones: what the beam hears, synthesised from the signatures in it.
        if (!listening) return;
        float lo = 0f, mid = 0f, hi = 0f;
        foreach (var h in _hydroSources)
        {
            var sig = Hydrophone.Of(h.Class);
            float wgt = h.Level * Hydrophone.BeamWeight(_beam, h.Bearing, Mathf.Max(6f, h.Spread));
            lo += wgt * sig.Low; mid += wgt * sig.Mid; hi += wgt * sig.High;
        }
        var g = _phone.Filter;
        _audio.HydLow = Mathf.Min(1f, lo) * (g is Hydrophone.Band.All or Hydrophone.Band.Low ? 1f : 0.1f);
        _audio.HydMid = Mathf.Min(1f, mid) * (g is Hydrophone.Band.All or Hydrophone.Band.Mid ? 1f : 0.1f);
        _audio.HydHigh = Mathf.Min(1f, hi) * (g is Hydrophone.Band.All or Hydrophone.Band.High ? 1f : 0.1f);
        var dom = src is HydroSource d ? Hydrophone.Of(d.Class) : default;
        _audio.HydPulseHz = dom.PulseHz;
        _audio.HydDuty = src == null ? 1f : dom.Duty;
        _audio.HydToneHz = dom.ToneHz;
        _audio.HydTone = src is HydroSource d2 ? dom.Tone * Mathf.Min(1f, d2.Level * 2f) : 0f;
        _audio.HydSelf = _listener.Noise;
    }

    // ------------------------------------------------------------------ the console

    private void EnterHydrophone()
    {
        _mode = Mode.Hydrophone;
        _player.InputEnabled = false;
        _hud.SetCrosshair(false);
        _hud.SetPrompt("");
        _hud.SetHelp("mouse / A D steer the beam · Q band · R hold to record · F fullscreen screen · E / ESC leave");
        _audio.PlayBlip(620f, 0.05f);
        Input.MouseMode = Input.MouseModeEnum.Captured;
        MoveStationCam(_interior.GlobalTransform * _interior.HydrophoneView);
        if (_phone.Recordings.Count == 0)
            _hud.Message("Headphones on. The sea is louder than you thought. Your own boat is the loudest thing in it.", 6f);
    }

    private void SteerBeam(float deg) => _beam = Hydrophone.Wrap(_beam + deg);

    private void UpdateHydrophone(float dt)
    {
        float steer = Input.GetAxis(GameInput.Left, GameInput.Right);
        if (Mathf.Abs(steer) > 0.01f) SteerBeam(steer * 70f * dt);
        if (Input.IsActionJustPressed(GameInput.SonarRange))
        {
            _phone.Filter = (Hydrophone.Band)(((int)_phone.Filter + 1) % 4);
            _audio.PlayBlip(900f, 0.02f, 0.3f);
        }
        // Hold R to record: the beam must stay on the sound for the whole take.
        if (Input.IsActionPressed(GameInput.Reload))
        {
            if (_recordT > -999f)   // -1000 = a take just finished: let go of R before the next one
            {
                if (_recordT < 0f) { _recordT = 0f; _audio.PlayBlip(1400f, 0.04f, 0.4f); }
                _recordT += dt;
                _hydroDisplay.RecordProgress = Mathf.Min(1f, _recordT / RecordSeconds);
                if (_recordT >= RecordSeconds) { FinishRecording(); _recordT = -1000f; }
            }
        }
        else { _recordT = -1f; _hydroDisplay.RecordProgress = -1f; }
        if (Input.IsActionJustPressed(GameInput.Fullscreen))
        {
            _feedFullscreen = !_feedFullscreen;
            _hud.ShowFeed(_feedFullscreen ? _hydroScope.GetTexture() : null, "HYDROPHONE  -  PASSIVE", 1f, "[A/D] steer   [Q] band   [R] hold to record   [F] close");
            _audio.PlayBlip(_feedFullscreen ? 800f : 600f, 0.05f);
        }
        if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact))
        {
            if (_feedFullscreen && Input.IsActionJustPressed(GameInput.Cancel)) { _feedFullscreen = false; _hud.ShowFeed(null, null, 1f); return; }
            _audio.Hydro = false;
            EnterWalking();
        }
    }

    private void FinishRecording()
    {
        _hydroDisplay.RecordProgress = -1f;
        var (src, _) = InBeam();
        if (src is not HydroSource s)
        {
            _hud.Message(_listener.Noise > 0.3f ? "Recorded: your own engine. Go quiet first." : "Recorded: nothing but the sea.", 4f);
            _audio.PlayBlip(300f, 0.08f, 0.4f);
            return;
        }
        // Close enough to see it (or sitting next to it) identifies it.
        bool identify = s.Class switch
        {
            SoundClass.Ravager or SoundClass.Ray or SoundClass.Wyrm or SoundClass.Gloam => s.Distance < 70f,
            SoundClass.Whale => s.Distance < 150f,   // impossible to miss at that range
            SoundClass.Station => s.Distance < 300f,
            SoundClass.Vent => s.Distance < 120f,
            _ => false,
        };
        var existing = _phone.MatchOf(s.Class);
        if (existing != null)
        {
            string id = "";
            if (identify && _phone.Identified.Add(s.Class)) id = $" Identified: {Hydrophone.Name(s.Class)}.";
            _hud.Message($"Same signature as REC {existing.No:00}.{id}", 5f);
            _audio.PlayBlip(1200f, 0.06f, 0.4f);
            return;
        }
        if (_phone.Recordings.Count >= Hydrophone.MaxRecordings) { _hud.Message("Library full."); return; }
        int no = _phone.Recordings.Count + 1;
        float compass = Mathf.PosMod(_sub.Heading + s.Bearing, 360f);
        _phone.Recordings.Add(new Recording(no, s.Class, compass, _sub.Depth, identify));
        if (identify) _phone.Identified.Add(s.Class);
        _audio.PlayBlip(1600f, 0.05f, 0.4f);
        _hud.Message(identify
            ? $"REC {no:00} stored: {Hydrophone.Name(s.Class)}."
            : $"REC {no:00} stored: {Hydrophone.Describe(s.Class)}, bearing {compass:000}°. Unidentified.", 6f);
        if (s.Class == SoundClass.Sounding)
            GetTree().CreateTimer(6.5).Timeout += () => _hud.Message("It's very faint. It's very regular. It sounds like counting.", 5f);
        Autosave("recording");
    }

    /// <summary>For the ending: did you record the Sounding before you knew what it was?</summary>
    private string SoundingRecordedLine()
    {
        var rec = _phone.MatchOf(SoundClass.Sounding);
        if (rec == null) return null;
        _phone.Identified.Add(SoundClass.Sounding);
        return $"The hydrophone flags a match: REC {rec.No:00}, recorded at {rec.Depth:0} m. You heard it on the first days. It was counting you even then.";
    }
}
