using System;
using Godot;
using Solitude.Core;
using Solitude.Creatures;

namespace Solitude;

/// <summary>
/// The Gloam's glue (see Creatures/Gloam.cs): when it comes, what it sees, what its hit does.
///
/// It only lives deep, and only comes now and then. It sees light: the boat's floods, the diver's
/// lamp. The answer is always the same and always hard: lights off, slow down, wait. The proximity
/// panel still works in the dark; the cameras mostly don't.
///
/// Its hit trips the flood circuit (it goes for the light), so after a strike you're dark whether
/// you like it or not, for a while. Outside, it cracks the diver's lamp.
/// F2 (debug) brings one in now.
/// </summary>
public partial class Main
{
    private Gloam _gloam;
    private float _gloamT = 720f;      // first chance after ~12 minutes of deep water
    private float _floodTrip;          // seconds until the floods can come back on after a hit
    private float _lampCrack;          // same for the diver's lamp
    private float _gloamRattleT;
    private static bool _toldGloam, _toldGloamLost, _toldGloamHit;
    private readonly Random _gloamRng = new();

    public const float GloamMinDepth = 420f;

    private float GloamDistance => _gloam?.DistanceToTarget ?? float.MaxValue;

    private void UpdateGloam(float dt)
    {
        _floodTrip = Mathf.Max(0f, _floodTrip - dt);
        _lampCrack = Mathf.Max(0f, _lampCrack - dt);
        if (_mode == Mode.Title || _ended) return;

        if (Input.IsActionJustPressed(GameInput.DebugGloam)) { SummonGloam(120f); _hud.Message("[debug] something plated is circling out in the dark", 3f); }

        bool diving = _diver.Active;
        float depth = diving ? -_diver.GlobalPosition.Y : _sub.Depth;
        bool sheltered = _stations.DockedAt != null || _player.InStation;

        if (_gloam == null)
        {
            if (depth > GloamMinDepth && !sheltered) _gloamT -= dt;
            if (_gloamT <= 0f)
            {
                _gloamT = 600f + (float)_gloamRng.NextDouble() * 600f;   // 10-20 minutes between chances
                bool busy = _creatures.Whale != null || _creatures.Wyrm != null || _listener.InEncounter;
                if (!busy && _gloamRng.NextDouble() < 0.7) SummonGloam(170f + (float)_gloamRng.NextDouble() * 60f);
            }
            return;
        }

        // Gone when it's had enough, when you're back in shallower water, or when the whale comes:
        // even this thing gets out of the way of the whale.
        if (!_gloam.Leaving && (depth < GloamMinDepth - 80f || (_creatures.Whale is { } w && w.DistanceToFocus < 250f))) _gloam.Leave();
        if (sheltered && !_gloam.Leaving && _gloam.Current is Gloam.State.Prowl or Gloam.State.Search) _gloam.Leave();
        if (_gloam.Leaving && GloamDistance > 320f) { _gloam.QueueFree(); _gloam = null; return; }

        // While it's close and hunting, its plates rattle through the hull now and then.
        _gloamRattleT -= dt;
        float d = GloamDistance;
        if (_gloamRattleT <= 0f && d < 160f && _gloam.Current != Gloam.State.Prowl)
        {
            _gloamRattleT = 7f + (float)_gloamRng.NextDouble() * 9f;
            Vector3 rel = _gloam.Head - FocusPoint();
            _audio.PlayAt("gloam", Mathf.Clamp(rel.X / Mathf.Max(1f, rel.Length()), -0.8f, 0.8f), Mathf.Clamp(1f - d / 160f, 0.15f, 0.9f));
        }
    }

    private void SummonGloam(float distance)
    {
        _gloam?.QueueFree();
        _gloam = new Gloam
        {
            Name = "Gloam", Gen = _gen, Audio = _audio,
            // While a lure flare burns where it can see it, the flare is the light it wants.
            Target = () => LureVisible ? _lure.GlobalPosition : FocusPoint(),
            Brightness = () => LureVisible ? 1.2f : GloamBrightness(),
            TargetSpeed = () => LureVisible ? 0f : _diver.Active ? _diver.Velocity.Length() : _sub.LinearVelocity.Length(),
            TargetIsDiver = () => !LureVisible && _diver.Active,
        };
        _game.AddChild(_gloam);
        Vector3 f = FocusPoint();
        float a = (float)_gloamRng.NextDouble() * Mathf.Tau;
        Vector3 at = f + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * distance;
        at.Y = Mathf.Max(f.Y + (float)_gloamRng.NextDouble() * 10f - 5f, _gen.Height(at.X, at.Z) + 8f);
        _gloam.Place(at, new Vector3(-MathF.Sin(a), 0, MathF.Cos(a)));
        _gloam.Noticed += OnGloamNoticed;
        _gloam.LostYou += OnGloamLost;
        _gloam.Struck += OnGloamStruck;
        _gloamRattleT = 2f;
    }

    /// <summary>What the Gloam can see of you: 1 = floods / lamp on, 0 = dark.</summary>
    private float GloamBrightness()
    {
        float murk = (1f - 0.85f * _silt) * (_sub.RedFloods && !_diver.Active ? 0.12f : 1f);   // light doesn't carry far in silt; deep red barely registers
        if (_diver.Active) return _diver.Lamp.Visible ? 0.6f * murk : 0f;
        return _sub.LightsOn ? murk : 0f;
    }

    private void OnGloamNoticed()
    {
        _audio.PlayAt("gloam", 0f, 0.8f);
        if (_toldGloam) return;
        _toldGloam = true;
        _hud.Message(_diver.Active
            ? "Something out there turned toward your lamp. Plates. Lots of plates. LAMP OFF. [F]"
            : "HYDROPHONE: something plated just turned toward the floodlights. FLOODS OFF [L]. Slow down.", 8f);
    }

    private void OnGloamLost()
    {
        if (_toldGloamLost) return;
        _toldGloamLost = true;
        _hud.Message("It lost the light. It's searching where it last saw you. Stay dark, stay slow.", 7f);
    }

    private void OnGloamStruck(float strength)
    {
        if (LureVisible && _gloam.Head.DistanceTo(_lure.GlobalPosition) < 16f) { LureEaten(); return; }
        if (_diver.Active)
        {
            _player.Damage(40f * strength);
            _diver.Lamp.Visible = false;
            _lampCrack = 30f;
            _audio.PlayImpact(2.5f);
            _hud.Message("It hit you. The lamp is cracked: dark for a while. Don't move.", 6f);
            return;
        }
        Vector3 push = _sub.GlobalPosition - _gloam.Head; push.Y = 0;
        push = push.LengthSquared() > 1e-3f ? push.Normalized() : Vector3.Right;
        _sub.Strike(2.6f + strength * 0.6f, push * 2600f);
        if (_gloamRng.NextDouble() < 0.5) _hull.OpenBreach();
        // It goes for the light: the flood circuit trips.
        _sub.LightsOn = false;
        _floodTrip = 45f;
        _audio.PlayGroan(1f);
        _hud.Message(_toldGloamHit
            ? "FLOOD CIRCUIT TRIPPED."
            : "It hit the boat. FLOOD CIRCUIT TRIPPED: resetting. Maybe don't turn them back on straight away.", 7f);
        _toldGloamHit = true;
    }

    /// <summary>Helm [L]: the floods won't come back while the circuit is resetting after a hit.</summary>
    private bool FloodsLocked()
    {
        if (_floodTrip <= 0f) return false;
        _hud.Message($"FLOOD CIRCUIT RESETTING... {_floodTrip:0} s", 2f);
        _audio.PlayAt("deny", 0f, 0.6f);
        return true;
    }

    /// <summary>Diver [F]: a cracked lamp takes a while to flicker back.</summary>
    private bool LampLocked()
    {
        if (_lampCrack <= 0f) return false;
        _hud.Message($"The lamp is cracked. It flickers, and doesn't catch. ({_lampCrack:0} s)", 2f);
        return true;
    }
}
