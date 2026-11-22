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
    private Vector3 _eyeOutward, _eyeTravel;
    private float _eyeLateral, _eyeRise;

    /// <summary>A hull camera's lens in the world, from the boat itself (the camera node lags a frame).</summary>
    private Vector3 LensWorld(int cam)
    {
        var (pos, dir) = Solitude.Sub.SubmarineModel.CameraPods[cam];
        return _sub.GlobalTransform * (pos + dir * 0.3f);
    }
    private readonly Random _eyeRng = new();

    private const float EyeGap = 3.2f;   // how close the eye comes to the lens (m)

    // ------------------------------------------------------------------ the whale is solid
    // Tom: "puppy-sized brain, skyscraper-sized body: not aggressive, but it can bump into your boat
    // and gently nudge it." Its body is seven spheres along the spine (model units, measured from
    // the mesh: x across, y up, z along the body, radius); the boat is a capsule along its hull.
    // The long pectoral fins reach ~5 units out and sweep down and back: they're what touches first.
    private static readonly (float x, float y, float z, float r)[] WhaleBody =
    {
        (0f, 0.5f, 8f, 1.4f), (0f, 0.1f, 6f, 1.9f), (0f, 0f, 3.5f, 2.1f), (0f, 0.5f, 1f, 2.0f), (0f, 1f, -2f, 1.6f), (0f, 1f, -5f, 1.0f), (0f, 1.8f, -8f, 1.2f),
        (2.6f, -0.8f, 3.6f, 0.7f), (3.8f, -1.9f, 2.6f, 0.6f), (4.9f, -2.9f, 1.6f, 0.5f),
        (-2.6f, -0.8f, 3.6f, 0.7f), (-3.8f, -1.9f, 2.6f, 0.6f), (-4.9f, -2.9f, 1.6f, 0.5f),
    };
    private const float HullRadius = 3.1f;
    private bool _whaleTouching;
    private float _bumpCd;
    private static bool _toldBump;

    private void UpdateWhaleContact(float dt)
    {
        _bumpCd -= dt;
        var w = _creatures.Whale;
        if (w == null || _mode == Mode.Title) { _whaleTouching = false; return; }
        var p = w.Profile;
        float s = p.Length / p.ModelLength;
        bool diving = _diver.Active;
        Vector3 a = _sub.ToGlobal(new Vector3(0, 0, -13f)), b = _sub.ToGlobal(new Vector3(0, 0, 13f));
        float deepest = 0f;
        Vector3 push = Vector3.Zero;
        foreach (var (x, y, z, r) in WhaleBody)
        {
            Vector3 c = w.ToGlobal(new Vector3(-x * s, y * s, -z * s));   // the model faces +Z; the node is turned 180°
            if (diving)
            {
                Vector3 d = _diver.GlobalPosition - c;
                float pen = r * s + 0.6f - d.Length();
                if (pen > deepest) { deepest = pen; push = d.Normalized(); }
            }
            else if (_stations.DockedAt == null)
            {
                Vector3 ab = b - a;
                float t = Mathf.Clamp((c - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
                Vector3 d = (a + ab * t) - c;
                float pen = r * s + HullRadius - d.Length();
                if (pen > deepest) { deepest = pen; push = d.LengthSquared() > 1e-4f ? d.Normalized() : Vector3.Up; }
            }
        }
        bool touching = deepest > 0f;
        if (touching)
        {
            // Not a hit: a lean. Move out at a speed that grows with how deep it pressed in.
            float want = Mathf.Min(0.15f + deepest * 0.3f, 0.3f);
            // ...and it yields: during the eye encounter its line backs off from the hull, so the boat
            // only sways while the whale makes room (otherwise it would follow the lens and push forever).
            if (_eyeActive && !diving) _eyeLateral += Mathf.Min(deepest + 0.2f, 1.5f) * 1.5f * dt;
            if (diving)
            {
                float vn = _diver.Velocity.Dot(push);
                if (vn < want) _diver.Velocity += push * (want - vn) * Mathf.Clamp(6f * dt, 0f, 1f);
            }
            else
            {
                float vn = _sub.LinearVelocity.Dot(push);
                if (vn < want) _sub.LinearVelocity += push * (want - vn) * Mathf.Clamp(2f * dt, 0f, 1f);
            }
        }
        // After a bump the water settles the boat again (a rigid body would drift on for ages).
        if (!touching && !diving && _bumpCd > 2f)
        {
            _sub.LinearVelocity *= 1f - Mathf.Clamp(0.9f * dt, 0f, 1f);
            _sub.AngularVelocity *= 1f - Mathf.Clamp(1.5f * dt, 0f, 1f);
        }
        if (touching && !_whaleTouching && _bumpCd <= 0f)
        {
            _bumpCd = 6f;
            GD.Print($"[whale] bump: pressed in {deepest:0.00} m, {(diving ? "diver" : "boat")}");
            _audio.PlayImpact(0.55f);
            _audio.PlayGroan(0.55f);
            if (diving) _audio.PlayAt("knock", 0f, 0.6f);
            else { PlaySpatial("knock", _sub.GlobalPosition); _player.AddShake(0.35f); }
            if (!_toldBump)
            {
                _toldBump = true;
                _hud.Message(diving ? "It nudges you aside like a fish nudging a pebble. It didn't mean anything by it."
                                    : "Something the size of a building just leaned on the boat. Gently. It didn't mean anything by it.", 7f);
            }
        }
        _whaleTouching = touching;
    }

    private void UpdateEyeEncounter(float dt)
    {
        if (_mode == Mode.Title || _ended) return;
        UpdateWhaleContact(dt);
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

        // Keep the line to wherever the lens is now (the boat drifts, or gets nudged by a fin).
        if (_eyePhase < 2)
            w.SetTransitTarget(target + _eyeOutward * _eyeLateral + Vector3.Up * _eyeRise + _eyeTravel * 500f);

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
        _eyeOutward = outward; _eyeTravel = travel;
        _eyeLateral = p.EyeModel.X * s + EyeGap; _eyeRise = -p.EyeModel.Y * s;
        Vector3 centreLine = lens + outward * _eyeLateral + Vector3.Up * _eyeRise;
        Vector3 start = centreLine - travel * 80f - travel * (p.EyeModel.Z * s);   // ~40 s from "motion" to the eye
        _creatures.SummonWhaleBetween(start, centreLine + travel * 500f);
        var w = _creatures.Whale;
        w.IgnoreSeabed = true;   // hold the line along the hull, whatever the bottom does
        _eyeActive = true;
        _eyePhase = 0;
        _eyeAge = 0f;
        if (!diving)
        {
            _rig.Centre(_eyeCam);   // the head swings back to its mount: whatever's coming, it comes to the lens
            SelectFeed(_eyeCam);
            _audio.PlayBlip(880f, 0.06f, 0.5f);
            _hud.Message($"MONITORS: motion on {_rig.Feeds[_eyeCam].Name.Replace("  ", " ")}.", 6f);
        }
    }
}
