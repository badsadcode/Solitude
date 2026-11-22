using System;
using Godot;
using Solitude.Core;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// CURRENTS AND SILT STORMS (ideas from BALE IX).
///
/// Currents: Systems/Currents.cs is the field; this feeds it to the boat (drag is through the
/// water, so a stopped boat drifts), the diver (you swim relative to the water), the snow, and
/// the helm's CURRENT line (speed and set, so you can see heading and track disagree).
/// Near the seabed the water is slower.
///
/// Silt storms: now and then a turbidity current comes down a slope. A rumble first (and the
/// hydrophone hears it coming), then the front: the water goes brown and closes in, a strong push,
/// sonar scatters into clutter, the hull cameras go to snow, grit hisses on the hull. Then it
/// settles. What else it changes is left for the player to find out.
/// F1 (debug) starts one.
/// </summary>
public partial class Main
{
    private Currents _currents;
    private enum SiltPhase { None, Warning, Storm, Clearing }
    private SiltPhase _siltPhase = SiltPhase.None;
    private float _siltNext = 840f, _siltPhaseT, _siltLen;
    /// <summary>0..1 how thick the water is right now (eased).</summary>
    private float _silt;
    private Vector3 _stormDir;
    private float _stormPush;
    private readonly Random _siltRng = new();

    public bool SiltStorm => _siltPhase is SiltPhase.Storm;
    /// <summary>How long the rumble comes before the front (much longer once the lab has studied sediment).</summary>
    private float SiltWarnLen => _lab.Has(Discovery.SiltForecast) ? 100f : 35f;

    private void SetupCurrents()
    {
        _currents = new Currents(Seed);
        _diver.WaterAt = p => WaterAt(p);
    }

    /// <summary>The water's velocity at a point: the field, slowed near the seabed.</summary>
    private Vector3 WaterAt(Vector3 p)
    {
        Vector3 w = _currents.At(p, _shipLog.Clock);
        float floor = _gen.Height(p.X, p.Z);
        return w * Mathf.Clamp((p.Y - floor) / 10f, 0.3f, 1f);
    }

    private void UpdateSilt(float dt)
    {
        if (_currents == null) return;
        bool title = _mode == Mode.Title;

        // --- the storm cycle
        if (!title && !_ended)
        {
            if (Input.IsActionJustPressed(GameInput.DebugSilt) && _siltPhase == SiltPhase.None) { StartSiltWarning(); _hud.Message("[debug] silt storm incoming", 3f); }
            float depth = _diver.Active ? -_diver.GlobalPosition.Y : _sub.Depth;
            if (_siltPhase == SiltPhase.None)
            {
                if (depth > 150f) _siltNext -= dt;
                if (_siltNext <= 0f)
                {
                    _siltNext = 900f + (float)_siltRng.NextDouble() * 720f;   // 15-27 minutes between chances
                    if (_siltRng.NextDouble() < 0.6 && !_listener.InEncounter) StartSiltWarning();
                }
            }
            else
            {
                _siltPhaseT += dt;
                switch (_siltPhase)
                {
                    case SiltPhase.Warning when _siltPhaseT > SiltWarnLen:
                        _siltPhase = SiltPhase.Storm; _siltPhaseT = 0f;
                        _siltLen = 100f + (float)_siltRng.NextDouble() * 80f;
                        _hud.Message(_diver.Active
                            ? "SILT. The world goes brown. You can't see your hands. The water is pulling. Find the hatch light."
                            : "SILT. The water goes brown against the glass and the hull starts to hiss.", 7f);
                        break;
                    case SiltPhase.Storm when _siltPhaseT > _siltLen:
                        _siltPhase = SiltPhase.Clearing; _siltPhaseT = 0f;
                        _hud.Message("The silt is settling. The hissing thins out.", 5f);
                        break;
                    case SiltPhase.Clearing when _siltPhaseT > 45f:
                        _siltPhase = SiltPhase.None; _siltPhaseT = 0f;
                        break;
                }
            }
        }

        float target = _siltPhase switch
        {
            SiltPhase.Warning => 0.05f * Mathf.Clamp(_siltPhaseT / SiltWarnLen, 0f, 1f),
            SiltPhase.Storm => 1f,
            SiltPhase.Clearing => 1f - Mathf.Clamp(_siltPhaseT / 45f, 0f, 1f),
            _ => 0f,
        };
        // The front arrives fast; the settling is slow.
        _silt = Mathf.MoveToward(_silt, target, dt * (target > _silt ? 0.09f : 0.03f));
        float push = _siltPhase == SiltPhase.Storm || _siltPhase == SiltPhase.Clearing ? _silt : 0f;
        float gust = 0.75f + 0.25f * Mathf.Sin(_shipLog.Clock * 0.7f) * Mathf.Sin(_shipLog.Clock * 0.23f);
        _currents.Gust = _stormDir * _stormPush * push * gust;

        // --- feed everything
        Vector3 focus = FocusPoint();
        _ocean.Silt = _silt;
        _ocean.Flow = WaterAt(focus);
        _rig.Murk = _silt;
        _sonar.Clutter = _silt;
        _audio.Silt = title ? 0f : (_siltPhase == SiltPhase.Warning ? Mathf.Clamp(_siltPhaseT / SiltWarnLen, 0f, 1f) * 0.25f : _silt) * (_player.InStation ? 0.3f : 1f);
        if (!_sub.Freeze) _sub.Water = title ? Vector3.Zero : WaterAt(_sub.GlobalPosition);
        else _sub.Water = Vector3.Zero;
    }

    private void StartSiltWarning()
    {
        _siltPhase = SiltPhase.Warning;
        _siltPhaseT = 0f;
        // From upslope: the direction the seabed falls away toward, roughly (sampled around the boat).
        Vector3 p = _diver.Active ? _diver.GlobalPosition : _sub.GlobalPosition;
        float h0 = _gen.Height(p.X, p.Z);
        Vector3 down = Vector3.Zero;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8f;
            Vector3 d = new(Mathf.Cos(a), 0, Mathf.Sin(a));
            down += d * (h0 - _gen.Height(p.X + d.X * 120f, p.Z + d.Z * 120f));
        }
        if (down.LengthSquared() < 1e-3f) { float a = (float)_siltRng.NextDouble() * Mathf.Tau; down = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)); }
        _stormDir = down.Normalized();
        _stormPush = 1.1f + (float)_siltRng.NextDouble() * 0.7f;
        _hud.Message(_lab.Has(Discovery.SiltForecast)
            ? "HYDROPHONE: a long, low rumble upslope. It's getting louder. (Silt signature: the front is maybe a minute and a half out.)"
            : "HYDROPHONE: a long, low rumble upslope. It's getting louder.", 7f);
        _audio.PlayGroan(0.6f);
    }

    /// <summary>For the hydrophone: the storm's roar, from upslope, while it's coming and while it's here.</summary>
    private bool SiltRumble(out Vector3 at, out float level)
    {
        Vector3 p = FocusPoint();
        at = p - _stormDir * (_siltPhase == SiltPhase.Warning ? 400f - _siltPhaseT * 10f : 40f);
        level = _siltPhase switch { SiltPhase.Warning => 0.4f + 0.5f * Mathf.Clamp(_siltPhaseT / SiltWarnLen, 0f, 1f), SiltPhase.Storm => 1f, SiltPhase.Clearing => _silt, _ => 0f };
        return level > 0.02f;
    }

    /// <summary>The helm's line: what the water is doing (speed over ground vs. through the water).</summary>
    private string CurrentHelmText()
    {
        if (_currents == null) return "";
        Vector3 w = _sub.Water; w.Y = 0;
        float spd = w.Length();
        string set = spd > 0.03f ? $"SET {Mathf.RoundToInt(Currents.SetDegrees(w)) % 360:000}°" : "SLACK";
        return $"\nCURRENT  {spd,4:0.00} m/s  {set}{(_silt > 0.3f ? "   !! SILT" : "")}";
    }
}
