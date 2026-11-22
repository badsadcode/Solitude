using System;
using Godot;
using Solitude.Core;
using Solitude.Creatures;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// THE EYE AT THE WINDOW (an idea from Thalassa): very rarely, while the boat is stopped or drifting
/// in deep water, the whale comes alongside, slows, and stops with one eye a few metres from a hull
/// camera. It looks. Then it goes. Nothing else happens. That's the point.
///
/// The boat flags "motion" on that camera and puts it on the big monitor. Outside in the suit, it
/// stops beside you instead. F3 (debug) starts one now.
/// </summary>
public partial class Main
{
    private float _eyeT = 900f;          // first chance after ~15 minutes
    private bool _eyeActive, _eyeDiver;
    private int _eyePhase, _eyeCam = -1;
    private float _eyeLinger, _eyeAge;

    /// <summary>A hull camera's lens in the world, from the boat itself (the camera node lags a frame).</summary>
    private Vector3 LensWorld(int cam)
    {
        var (pos, dir) = Solitude.Sub.SubmarineModel.CameraPods[cam];
        return _sub.GlobalTransform * (pos + dir * 0.3f);
    }
    private readonly Random _eyeRng = new();

    private const float EyeGap = 3.2f;   // how close the eye comes to the lens (m)

    private void UpdateEyeEncounter(float dt)
    {
        if (_mode == Mode.Title || _ended) return;
        if (Input.IsActionJustPressed(GameInput.DebugEye)) { StartEyeEncounter(_diver.Active); return; }
        if (!_eyeActive)
        {
            _eyeT -= dt;
            if (_eyeT > 0f) return;
            _eyeT = 600f + (float)_eyeRng.NextDouble() * 900f;    // a chance every 10-25 minutes
            bool diving = _diver.Active;
            bool quietDeep = diving ? -_diver.GlobalPosition.Y > 250f && _diver.TimeOutside > 40f
                                    : !_player.InStation && _sub.Depth > 300f && _sub.LinearVelocity.Length() < 1f;
            bool clear = _stations.DockedAt == null && _creatures.Whale == null && _creatures.Wyrm == null && _listener.Current <= Listener.Tier.Stirring;
            if (quietDeep && clear && _eyeRng.NextDouble() < 0.5) StartEyeEncounter(diving);
            return;
        }

        var w = _creatures.Whale;
        if (w == null) { _eyeActive = false; return; }
        Vector3 target = _eyeDiver ? _diver.Camera.GlobalPosition : LensWorld(_eyeCam);
        _eyeAge += dt;
        Vector3 eL = w.EyeWorld(-1), eR = w.EyeWorld(1);
        Vector3 eye = eL.DistanceTo(target) < eR.DistanceTo(target) ? eL : eR;
        float ahead = (target - eye).Dot(-w.GlobalBasis.Z);      // how far the eye still has to travel
        w.EyeTarget = eye.DistanceTo(target) < 70f ? target : null;

        switch (_eyePhase)
        {
            case 0: // coming alongside, slowing as the eye nears the lens
                // Slowing as the eye nears the lens, but never to a crawl: it arrives, then it stops.
                w.SpeedOverride = ahead > 45f ? 2.4f : Mathf.Lerp(0.5f, 2.4f, Mathf.Clamp(ahead / 45f, 0f, 1f));
                if (ahead < 1.2f && _eyeAge > 1f)   // (give it a moment to turn onto its line first)
                {
                    _eyePhase = 1;
                    _eyeLinger = 5f + (float)_eyeRng.NextDouble() * 3f;
                    // It's right there. A low call through the hull; the hull answers.
                    _audio.PlayAt("whalesong", 0f, 1f);
                    _audio.PlayGroan(0.6f);
                    if (!_eyeDiver) _player.AddShake(0.25f);
                }
                break;
            case 1: // it stops and looks
                w.SpeedOverride = 0.04f;
                _eyeLinger -= dt;
                if (_eyeLinger <= 0f) { _eyePhase = 2; w.SpeedOverride = 1.6f; }
                break;
            case 2: // and goes, without hurrying
                if (ahead < -60f) { w.EyeTarget = null; w.SpeedOverride = null; _eyeActive = false; }
                break;
        }
    }

    /// <summary>
    /// Dev: `-- --start=dive --eye-test [--eye-shot=dir]` starts the diver's encounter after a few seconds,
    /// logs the eye's distance to the helmet, saves a frame each log, and quits.
    /// </summary>
    private void EyeTest()
    {
        string dir = null;
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith("--eye-shot=")) dir = a.Substring(11);
        var tw = CreateTween();
        tw.TweenInterval(6.0);
        tw.TweenCallback(Callable.From(() => { StartEyeEncounter(_diver.Active); GD.Print($"[eye-test] started, diving={_diver.Active}"); }));
        for (int i = 1; i <= 14; i++)
        {
            int k = i;
            tw.TweenInterval(4.0);
            tw.TweenCallback(Callable.From(() =>
            {
                var w = _creatures.Whale;
                if (w == null) { GD.Print("[eye-test] no whale"); return; }
                Vector3 me = _diver.Active ? _diver.Camera.GlobalPosition : LensWorld(_eyeCam);
                float d = Mathf.Min(w.EyeWorld(-1).DistanceTo(me), w.EyeWorld(1).DistanceTo(me));
                GD.Print($"[eye-test] t={k * 4} phase {_eyePhase} eye {d:0.0} m from you");
                if (dir != null) { DirAccess.MakeDirRecursiveAbsolute(dir); GetViewport().GetTexture().GetImage().SavePng($"{dir}/eye_{k:00}.png"); }
            }));
        }
        tw.TweenCallback(Callable.From(() => GetTree().Quit()));
    }

    private void StartEyeEncounter(bool diving)
    {
        _creatures.DismissWhale();
        Vector3 lens, outward, travel;
        _eyeDiver = diving;
        if (diving)
        {
            lens = _diver.Camera.GlobalPosition;
            float a = (float)_eyeRng.NextDouble() * Mathf.Tau;
            outward = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            travel = new Vector3(-outward.Z, 0, outward.X);
        }
        else
        {
            // Port (CAM 2) or starboard (CAM 3): it comes from ahead and slides aft along that side.
            _eyeCam = _eyeRng.NextDouble() < 0.5 ? 1 : 2;
            lens = LensWorld(_eyeCam);
            outward = _sub.GlobalBasis.X * (_eyeCam == 1 ? -1f : 1f); outward.Y = 0; outward = outward.Normalized();
            travel = _sub.GlobalBasis.Z; travel.Y = 0; travel = travel.Normalized();
        }
        // Put the whale's centre where its eye will pass ~4.5 m from the lens.
        var p = CreatureProfile.Whale();
        float s = p.Length / p.ModelLength;
        Vector3 centreLine = lens + outward * (p.EyeModel.X * s + EyeGap) + Vector3.Up * (-p.EyeModel.Y * s);
        Vector3 start = centreLine - travel * 80f - travel * (p.EyeModel.Z * s);   // ~40 s from "motion" to the eye
        _creatures.SummonWhaleBetween(start, centreLine + travel * 500f);
        var w = _creatures.Whale;
        w.IgnoreSeabed = true;   // hold the line along the hull, whatever the bottom does
        _eyeActive = true;
        _eyePhase = 0;
        _eyeAge = 0f;
        if (!diving)
        {
            SelectFeed(_eyeCam);
            _audio.PlayBlip(880f, 0.06f, 0.5f);
            _hud.Message($"MONITORS: motion on {_rig.Feeds[_eyeCam].Name.Replace("  ", " ")}.", 6f);
        }
    }
}
