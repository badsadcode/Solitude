using System;
using Godot;
using Solitude.Creatures;
using Solitude.Sonar;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// The Listener's glue (see Systems/Listener.cs): measures how loud you are, feeds it, and turns its
/// tiers into things that happen: distant calls, more knocks, answered pings, hunting creatures,
/// and the encounter where something vast passes beneath you and you hold your breath.
/// </summary>
public partial class Main
{
    private readonly Listener _listener = new();
    private float _huntT = 60f, _encounterBeat;
    private readonly Random _listenRng = new();
    private bool _toldAboutAnswer;

    /// <summary>For the helm readout: noise as hydrophone decibels (~60 quiet .. ~140 screaming).</summary>
    private float NoiseDb => 60f + Mathf.Min(_listener.Noise, 1f) * 80f;

    private void SetupListener()
    {
        _sonar.Pinged += OnPingHeard;
        _sub.Impact += s => _listener.AddPulse(s * 0.35f);
        _listener.TierChanged += OnListenerTier;
        _listener.EncounterStarted += OnEncounterStart;
        _listener.EncounterEnded += OnEncounterEnd;
    }

    private float StoryUnease => _endingRunning ? 1f : Mathf.Min(_coreSites.Count, CoresNeeded) / (float)CoresNeeded;

    private void UpdateListener(float dt)
    {
        if (_mode == Mode.Title || _ended) return;
        bool diving = _diver.Active;
        float noise;
        if (diving)
            noise = 0.03f + _diver.Exertion * 0.16f + (_diver.Lamp.Visible ? 0.02f : 0f);
        else
        {
            float engine = Mathf.Pow(Mathf.Abs(_sub.EngineOutput), 1.5f) * 0.55f * (_modules.Has(ModuleId.SilentDrive) ? 0.4f : 1f);
            noise = engine
                  + Mathf.Clamp(_sub.LinearVelocity.Length() / 6f, 0f, 1f) * 0.06f
                  + (_sub.LightsOn ? 0.05f : 0f)
                  + (_systems.IntakeOn ? 0.04f : 0f)
                  + (_systems.ArmActive ? 0.06f : 0f)
                  + (_hull.ActiveBreaches > 0 ? 0.2f : 0f);   // the klaxon
        }
        bool shielded = _stations.DockedAt != null || _player.InStation;
        float depth = diving ? Mathf.Max(0f, -_diver.GlobalPosition.Y) : _sub.Depth;
        _listener.Update(dt, noise, depth, shielded, StoryUnease * 25f);
        // The whale's song fills the water: while it's near, the Listener loses track of one small boat.
        if (_creatures.Whale is { } whale && whale.DistanceToFocus < 500f)
            _listener.Attention = Mathf.Max(StoryUnease * 25f, _listener.Attention - 1.5f * dt);
        // ...and so does a silt storm's roar.
        if (_silt > 0.5f)
            _listener.Attention = Mathf.Max(StoryUnease * 25f, _listener.Attention - 1.0f * _silt * dt);

        // Everything that already scales with "unease" (distant calls, hull knocks) follows attention too.
        _audio.Unease = Mathf.Max(StoryUnease, _listener.Attention / 100f * 0.85f);

        if (shielded) return;
        if (_listener.InEncounter) { UpdateEncounter(dt, diving); return; }

        // Hunting: the things that live here come looking.
        if (_listener.Current >= Listener.Tier.Hunting)
        {
            _huntT -= dt;
            if (_huntT <= 0f)
            {
                _huntT = 80f + (float)_listenRng.NextDouble() * 60f;
                if (diving) _ravager.DiverPass();
                else _ravager.ForcePass();
                if (depth > CreatureDirector.DeepThreshold && _creatures.Wyrm == null && _listenRng.NextDouble() < 0.35) _creatures.SummonWyrm();
            }
        }
        else _huntT = Mathf.Max(_huntT, 40f);
    }

    private void OnPingHeard()
    {
        _listener.HeardPing();
        if (_listener.Attention < 30f || _listenRng.NextDouble() > 0.2 + _listener.Attention / 250f) return;
        // It answers. One echo, from somewhere nothing is, a few seconds after yours should have died.
        float delay = 2.5f + (float)_listenRng.NextDouble() * 2.5f;
        float a = (float)_listenRng.NextDouble() * Mathf.Tau;
        Vector3 p = _sub.GlobalPosition + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * _sonar.Range * (0.45f + (float)_listenRng.NextDouble() * 0.4f);
        _sonar.AddPhantom(new Vector2(p.X, p.Z), SonarSystem.Now + delay, 10, 14f);
        _audio.PlayEcho(delay, 1f);
        if (!_toldAboutAnswer)
        {
            _toldAboutAnswer = true;
            GetTree().CreateTimer(delay + 0.8).Timeout += () => _hud.Message("Something answered the ping. There was nothing there.", 6f);
        }
    }

    private void OnListenerTier(Listener.Tier from, Listener.Tier to)
    {
        if (_mode == Mode.Title) return;
        string msg = (from, to) switch
        {
            (Listener.Tier.Calm, Listener.Tier.Stirring) => "HYDROPHONE: a low call, very far off. It started when you did.",
            (_, Listener.Tier.Hunting) when from < to => "HYDROPHONE: closer. Every ping, every turn of the screw — it hears it. Go quiet.",
            (_, Listener.Tier.Calm) => "Quiet now. Whatever it was has lost the thread.",
            _ => null,
        };
        if (msg != null) _hud.Message(msg, 6f);
        if (to == Listener.Tier.Hunting && from < to) _audio.PlayAt("listener", (float)(_listenRng.NextDouble() - 0.5), 0.45f);
    }

    private void OnEncounterStart()
    {
        _encounterBeat = 0f;
        _audio.Duck(-6f, 2f);
        _audio.PlayAt("listener", 0f, 1f);
        _hud.Message(_diver.Active
            ? "The water around you is moving. Something below is very, very large. Stay still. Breathe slow."
            : "!! Something is rising beneath the boat. It is very large. ALL STOP. LIGHTS OFF. NOT A SOUND.", 8f);
        // On the sonar: a wall of return, right below, where the seabed should be.
        Vector3 c = _diver.Active ? _diver.GlobalPosition : _sub.GlobalPosition;
        for (int i = 0; i < 6; i++)
        {
            float a = (float)_listenRng.NextDouble() * Mathf.Tau;
            var q = new Vector2(c.X, c.Z) + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (20f + i * 12f);
            _sonar.AddPhantom(q, SonarSystem.Now + i * 0.6, 14, 18f);
        }
    }

    private void UpdateEncounter(float dt, bool diving)
    {
        float u = _listener.EncounterT / Listener.EncounterLength;   // 0 → 1 as it arrives
        // It gets closer: the boat trembles, the hull complains, the low drone returns.
        _encounterBeat -= dt;
        if (_encounterBeat <= 0f)
        {
            _encounterBeat = 3.2f - u * 1.6f;
            _audio.PlayGroan(0.3f + u * 0.7f);
            if (u > 0.35f) _audio.PlayAt("listener", (float)(_listenRng.NextDouble() - 0.5) * 0.6f, 0.4f + u * 0.5f);
        }
        if (diving) _diver.Velocity += Vector3.Down * 0.15f * u * dt;   // the water sinks with it
        else _player.AddShake(0.05f * u * dt * 10f);
        if (Mathf.Abs(_listener.EncounterT - Listener.EncounterLength * 0.55f) < dt * 0.6f)
            _hud.Message("It's right under you.", 4f);
    }

    private void OnEncounterEnd(bool passed)
    {
        _audio.Duck(0f, 3f);
        if (passed)
        {
            _audio.PlayGroan(0.4f);
            _hud.Message("It passes. Slowly. It didn't hear you. You realise you haven't breathed in a while.", 7f);
            return;
        }
        _audio.PlayAt("listener", 0f, 1f);
        if (_diver.Active)
        {
            _player.Damage(30f);
            _diver.Velocity += new Vector3((float)_listenRng.NextDouble() - 0.5f, 1.2f, (float)_listenRng.NextDouble() - 0.5f).Normalized() * 7f;
            _audio.PlayImpact(2f);
            _hud.Message("It brushes past you. Just brushes. Your suit groans and something in your chest cracks.", 7f);
        }
        else
        {
            Vector3 side = _sub.GlobalBasis.X * (_listenRng.NextDouble() < 0.5 ? -1f : 1f);
            _sub.Strike(2.4f, (side + Vector3.Up * 0.6f) * _sub.Mass * 2.5f);
            _hud.Message("IT FOUND YOU. The whole boat lurches sideways like a kicked can.", 7f);
        }
    }

    private string ListenerVitals() =>
        _mode == Mode.Title || _diver.Active ? "" : $"    NOISE {NoiseDb:0} dB{(_listener.Noise < Listener.Quiet ? " (SILENT)" : "")}";
}
