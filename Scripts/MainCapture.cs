using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Solitude.Core;
using Solitude.Interior;
using Solitude.Sub;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// Dev harness: run with `-- --capture=&lt;dir&gt;` and the game walks itself through every
/// mode, saving screenshots and telemetry, then quits. Lets an AI (or CI) eyeball builds.
/// </summary>
public partial class Main
{
    private string _captureDir;
    private float _captureT;
    private int _captureStep;
    private List<(float at, Action act)> _captureScript;
    private Camera3D _docCam;
    private Solitude.World.TerrainStreamer.Pickup _capturePk;
    private float _docSide = 1f;
    private Node3D _docTarget;
    private float _docScale = 1f;
    private float _docForward;

    /// <summary>
    /// Dev shortcut: `-- --start=dive` (suited up, outside the boat) or `-- --start=station`
    /// (docked at the starter station and aboard). Skips the walk to the thing you're testing.
    /// </summary>
    private void SetupDebugStart()
    {
        string start = null;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--start=")) start = arg.Substring(8);
        if (start == null) return;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--stairs-test=")) StairsTest(arg.Substring(14));

        var tw = CreateTween();
        tw.TweenInterval(1.0);
        tw.TweenCallback(Callable.From(() =>
        {
            _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
            _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
            if (start == "dive")
            {
                _suitOn = true;
                _player.SpeedFactor = 0.7f;
                _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot);
                TryDiveFromHatch();
            }
            else if (start == "station")
            {
                var berth = _stations.Directory.Starter.DockPose;
                _sub.GlobalTransform = new Transform3D(berth.Basis, berth.Origin + berth.Basis.Z * 5f);
                var t2 = CreateTween();
                t2.TweenInterval(0.5);
                t2.TweenCallback(Callable.From(() => _stations.TryDock(null)));
                t2.TweenInterval(5.5);
                t2.TweenCallback(Callable.From(() => { _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot); OnHatch(); }));
            }
        }));
    }

    /// <summary>
    /// Dev: `-- --start=station --layout=outpost_01 --stairs-test=29.5,1.6,-65,0` puts the player at a
    /// station-local spot facing yaw (deg), holds W for 6 s and prints the height every half second.
    /// </summary>
    private void StairsTest(string spec)
    {
        _stairsView = spec.StartsWith("view:") ? spec.Substring(5) : null;   // view:prop:console, view:viewport, view:bulkhead
        if (_stairsView != null) spec = "airlock";
        bool airlock = spec is "airlock" or "airlock-walk";   // stand at the airlock and look (with --stairs-shot); -walk: then walk in
        _stairsWalkIn = spec == "airlock-walk";
        if (airlock) spec = "0,0,0,0";
        _stairsAirlock = airlock;
        var p = spec.Split(',');
        var local = new Vector3(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
        float yaw = Mathf.DegToRad(float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture));
        // Wait until we're really inside a station.
        var poll = new Timer { WaitTime = 0.5, Autostart = true };
        AddChild(poll);
        float waited = 0f;
        poll.Timeout += () =>
        {
            waited += 0.5f;
            if (Mathf.PosMod(waited, 5f) < 0.01f) GD.Print($"[stairs] waiting: docked={_stations.DockedAt?.Name ?? "no"} station={_station != null} inStation={_player.InStation} input={_player.InputEnabled} mode={_mode}");
            if (waited > 40f) { GD.Print("[stairs] never got inside a station"); GetTree().Quit(); }
            if (_station == null || !_player.InStation || !_player.InputEnabled) return;
            poll.QueueFree();
            StairsWalk(local, yaw);
        };
    }

    private bool _stairsAirlock, _stairsWalkIn;
    private string _stairsView;

    private void StairsWalk(Vector3 local, float yaw)
    {
        var tw = CreateTween();
        tw.TweenInterval(1.0);
        tw.TweenCallback(Callable.From(() =>
        {
            _player.GlobalTransform = _stairsAirlock ? _station.GlobalTransform * _station.AirlockPose : new Transform3D(new Basis(Vector3.Up, yaw), _station.ToGlobal(local));
            if (_stairsView != null)
            {
                var parts = _stairsView.Split(':');
                var v = _station.ViewOf(parts[0], parts.Length > 1 ? parts[1] : null, parts.Length > 2 && parts[2] == "broken");   // view:prop:locker:broken
                if (v is Transform3D vt) _player.GlobalTransform = _station.GlobalTransform * vt;
                else GD.Print($"[stairs] no marker for {_stairsView}");
            }
            _player.SetLook(0f, OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--stairs-shot=")) ? 0.05f : -0.3f);
            if (OS.GetCmdlineUserArgs().Any(x => x == "--stairs-torch")) _player.Flashlight.Visible = true;
            if (!_stairsAirlock || _stairsWalkIn) Input.ActionPress(GameInput.Forward);
            GD.Print($"[stairs] start local {local} layout {_station.Site.Layout}");
        }));
        for (int i = 1; i <= 20; i++)
        {
            tw.TweenInterval(0.5);
            int k = i;
            tw.TweenCallback(Callable.From(() =>
            {
                var l = _station.ToLocal(_player.GlobalPosition);
                GD.Print($"[stairs] t={k * 0.5f:0.0} local=({l.X:0.00}, {l.Y:0.00}, {l.Z:0.00}) onFloor={_player.IsOnFloor()} onWall={_player.IsOnWall()} step={Solitude.Core.Stepper.Why} hidden={_player.Hidden} crawler={(_station.Crawlers.Count > 0 ? $"{_station.Crawlers[^1].Current} {_station.NearestCrawler():0.0} m" : "-")} hp={_player.Health:0}");
                foreach (var arg in OS.GetCmdlineUserArgs())
                {
                    if (k == 1 && arg.StartsWith("--stairs-hide="))
                    {
                        // Hide, then send a crawler to where we were last "heard".
                        bool ok = _station.DebugHide(int.Parse(arg.Substring(14)));
                        if (OS.GetCmdlineUserArgs().Any(x => x == "--stairs-torch")) _player.Flashlight.Visible = true;
                        GD.Print($"[hide] hid={ok} spots={_station.HidingSpotCount} hidden={_player.Hidden}");
                        Vector3 back = _player.GlobalBasis.Z; back.Y = 0;
                        var away = _player.GlobalPosition + new Vector3(6f, 0.3f, 0f);
                        _station.DebugSpawnCrawler(away, true);
                        _station.Crawlers[^1].Hear(_player.GlobalPosition, 50f);
                    }
                    if (k == 1 && arg.StartsWith("--stairs-crawler="))
                    {
                        Vector3 ahead = -_player.GlobalBasis.Z; ahead.Y = 0;
                        _station.DebugSpawnCrawler(_player.GlobalPosition + ahead.Normalized() * 5f + Vector3.Up * 0.3f, arg.Contains("abyssal"), arg.Contains(":") ? int.Parse(arg[(arg.LastIndexOf(':') + 1)..]) : 0); _player.RotateY(0.55f); _player.SetLook(0f, -0.25f);
                        Input.ActionRelease(GameInput.Forward);
                    }
                    if (arg.StartsWith("--stairs-shots=")) GetViewport().GetTexture().GetImage().SavePng($"{arg.Substring(15)}_{k:00}.png");
                }
                if (k == 2 && OS.GetCmdlineUserArgs().Any(x => x == "--stairs-restore")) { _systems.Hold.Add(ItemId.PowerCell); _station.DebugUseJunction(); }
                if (k == 12)
                    foreach (var arg in OS.GetCmdlineUserArgs())
                        if (arg.StartsWith("--stairs-shot2=")) GetViewport().GetTexture().GetImage().SavePng(arg.Substring(15));
                if (k == (_stairsWalkIn ? 5 : 1))
                    foreach (var arg in OS.GetCmdlineUserArgs())
                        if (arg.StartsWith("--stairs-shot=")) GetViewport().GetTexture().GetImage().SavePng(arg.Substring(14));
            }));
        }
        tw.TweenCallback(Callable.From(() => { Input.ActionRelease(GameInput.Forward); GetTree().Quit(); }));
    }

    /// <summary>
    /// Dev: `-- --station-sweep` loads every baked station once (offscreen, at StationOrigin), checks
    /// there is floor under the airlock and prints what it built, then quits.
    /// </summary>
    private void StationSweep()
    {
        var names = Solitude.Stations.StationBank.All;
        int i = -1, bad = 0;
        Solitude.Stations.StationInterior cur = null;
        float wait = 0f;
        ulong t0 = 0;
        var timer = new Timer { WaitTime = 0.1, Autostart = true };
        AddChild(timer);
        timer.Timeout += () =>
        {
            if (cur != null && !cur.Ready && (wait += 0.1f) < 15f) return;
            if (cur != null)
            {
                var airlock = cur.GlobalTransform * cur.AirlockPose;
                var hit = cur.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(airlock.Origin + Vector3.Up * 0.5f, airlock.Origin + Vector3.Down * 3f, Layers.Interior));
                bool ok = cur.Ready && hit.Count > 0;
                if (!ok) bad++;
                int loot = 0, doors = 0;
                foreach (var n in cur.GetChildren()) if (n is Interactable it) { if (it.Prompt.Contains("door") || it.Prompt.Contains("panel")) doors++; else loot++; }
                int props = 0, bulk = 0;
                foreach (var n in cur.GetChildren()) { if (n is Solitude.Stations.SlideDoor) bulk++; else if (n.Name.ToString().StartsWith("prop_") || n.GetChildCount() > 0 && n is Node3D && n.GetParent() == cur && n.Name.ToString().Contains("prop")) props++; }
                GD.Print($"[sweep] {names[i],-12} {(ok ? "OK " : "BAD")} ready={cur.Ready} floorUnderAirlock={hit.Count > 0} interactables={loot} props={props} bulkheads={bulk} crawlers={cur.Crawlers.Count} powered={cur.Powered} {(Time.GetTicksMsec() - t0)} ms");
                cur.QueueFree();
                cur = null;
                return;   // next tick: the next station
            }
            if (++i >= names.Length) { GD.Print($"[sweep] done: {names.Length - bad}/{names.Length} OK"); GetTree().Quit(); return; }
            wait = 0f;
            t0 = Time.GetTicksMsec();
            Solitude.Stations.StationDirectory.ForceLayout = names[i];
            var site = new Solitude.Stations.StationSite { Id = 9000 + i, Layout = names[i], Name = "SWEEP", Seed = 1234 + i };
            cur = new Solitude.Stations.StationInterior
            {
                Name = "Sweep", Site = site, State = new Solitude.Stations.StationState(),
                Player = _player, Systems = _systems, Weapons = _weapons,
            };
            _game.AddChild(cur);
            cur.GlobalPosition = StationOrigin;
        };
    }

    private void SetupCapture()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--capture=")) _captureDir = arg.Substring(10);
        if (_captureDir == null) return;
        DirAccess.MakeDirRecursiveAbsolute(_captureDir);
        CallDeferred(MethodName.ApplyCaptureFrom);
        _audio.StartDebugRecording();

        float t = 7f;
        _captureScript = new()
        {
            (t, () => Shot("01_bridge_walking")),
            (t += 0.2f, () => { _player.Position = new Vector3(0.3f, 0.05f, -1.2f); _player.SetLook(Mathf.Pi * 0.35f, -0.1f); }),
            (t += 1.0f, () => Shot("02_sensor_bay")),
            (t += 0.2f, () => { _player.Position = new Vector3(0f, 0.05f, 4.2f); _player.SetLook(0f, -0.15f); }),
            (t += 1.0f, () => Shot("03_galley_forward")),
            (t += 0.2f, () => { _player.Position = new Vector3(-0.3f, 0.05f, 8.2f); _player.SetLook(Mathf.Pi, -0.1f); }),
            (t += 1.0f, () => Shot("04_engineering")),
            (t += 0.2f, () => { _player.Position = new Vector3(-0.5f, 0.05f, -3f); EnterMonitors(); }),
            (t += 1.5f, () => Shot("05_monitor_wall")),
            (t += 0.1f, () => SelectFeed(3)),
            (t += 0.6f, () => { _feedFullscreen = true; var f = _rig.Feeds[_selectedFeed]; _hud.ShowFeed(f.Viewport.GetTexture(), f.Name, f.Signal); }),
            (t += 0.6f, () => Shot("06_feed_fullscreen_keel")),
            // --- Milestone 2: sonar + holo
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(1.2f, 0.05f, -1.2f); EnterSonar(); }),
            (t += 1.0f, () => TryPing()),
            (t += 0.6f, () => Shot("06b_sonar_wavefront")),
            (t += 1.4f, () => Shot("06c_sonar_returns")),
            (t += 0.1f, () => { _feedFullscreen = true; _hud.ShowFeed(_sonarScope.GetTexture(), "SONAR  -  ACTIVE", 1f, "[SPACE] ping   [Q] range   [F] close"); }),
            (t += 0.4f, () => Shot("06d_sonar_fullscreen")),
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(0.2f, 0.05f, -2.0f); EnterHolo(); }),
            (t += 1.2f, () => Shot("06e_holo_after_ping")),
            (t += 0.1f, () => { _holoPitch = 1.2f; }),
            (t += 0.8f, () => Shot("06f_holo_top")),
            (t += 0.1f, () => { _holoPitch = 0.5f; _interior.Holo.ZoomIndex = 2; TryPing(); }),
            (t += 0.7f, () => Shot("06g_holo_reveal_wide")),
            (t += 0.1f, () => EnterWalking()),
            // --- Milestone 3: stations, then the arm on a real pickup
            (t += 0.1f, () =>
            {
                var h = _systems.Hold;
                h.Add(ItemId.Nodule, 4); h.Add(ItemId.Crystal, 3); h.Add(ItemId.Lumen, 2);
                h.Add(ItemId.Sediment, 1); h.Add(ItemId.Plankton, 4); h.Add(ItemId.Clam, 2);
                _player.Position = new Vector3(0.3f, 0.05f, 7f);
                EnterPanel("workbench");
            }),
            (t += 1.2f, () => Shot("13a_workbench")),
            (t += 0.1f, () => { PickPanel(0); PickPanel(0); }),
            (t += 0.5f, () => Shot("13b_workbench_after")),
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(0.3f, 0.05f, 2.4f); EnterPanel("galley"); }),
            (t += 1.2f, () => Shot("13c_galley")),
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(0f, 0.05f, 11.5f); EnterPanel("engineering"); }),
            (t += 1.2f, () => Shot("13d_engineering")),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _capturePk = _terrain.NearestPickup(_sub.GlobalPosition, 250f);
                if (_capturePk != null)
                {
                    var b = _sub.GlobalBasis;
                    _sub.GlobalPosition = _capturePk.Position - b * (ManipulatorArm.Shoulder + new Vector3(0, -2.6f, -3.6f)) + Vector3.Up * 1.0f;
                    _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                    _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                    GD.Print($"[capture] arm test target: {_capturePk.Type} at {_capturePk.Position}");
                }
                else GD.Print("[capture] arm test: no pickup found nearby");
                EnterArm();
            }),
            (t += 3.0f, () => Shot("14a_arm_deployed")),
            (t += 0.1f, () => { if (_capturePk != null) _arm.Target = _sub.ToLocal(_capturePk.Position) + Vector3.Up * 0.4f; }),
            (t += 3.0f, () => Shot("14b_arm_over_target")),
            (t += 0.1f, () => GD.Print($"[capture] grab={_arm.Grab()} tipDist={_arm.NearestDistance:0.00}")),
            (t += 1.0f, () => Shot("14c_arm_grabbed")),
            (t += 5.0f, () => { Shot("14d_arm_redeployed"); GD.Print($"[capture] hold weight {_systems.Hold.Weight}, power {_systems.Power:0.0}"); }),
            (t += 0.1f, () => EnterWalking()),
            // --- Milestone 4: approach, dock, board, fight, loot, read, return
            (t += 0.1f, () =>
            {
                var site = _stations.Directory.Starter;
                var berth = site.DockPose;
                _sub.GlobalTransform = new Transform3D(berth.Basis, berth.Origin + berth.Basis.Z * 45f);
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                _sub.TelegraphIndex = 3; _sub.ApplyTelegraph();
                GD.Print($"[capture] starter station: {site.Name} layout={site.Layout} at {site.Position} dist from spawn {site.Position.DistanceTo(_spawnPoint):0}");
                EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
            }),
            (t += 5.0f, () => { Shot("15a_approach_chase"); GD.Print($"[capture] docking: dist={_stations.DockDistance:0.0} lat={_stations.DockLateral:0.0} vert={_stations.DockVertical:0.0} hdg={_stations.DockHeadingError:0} capture={_stations.InCaptureZone}"); }),
            (t += 0.1f, () => { _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 1.0f, () => Shot("15b_approach_bow")),
            (t += 0.1f, () =>
            {
                var berth = _stations.Directory.Starter.DockPose;
                _sub.GlobalTransform = new Transform3D(berth.Basis, berth.Origin + berth.Basis.Z * 6f + berth.Basis.X * 1.2f);
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
            }),
            (t += 0.6f, () => GD.Print($"[capture] capture zone={_stations.InCaptureZone} dock={_stations.TryDock(null)}")),
            (t += 0.1f, () => { _view = HelmView.Chase; ApplyHelmView(); }),
            (t += 5.5f, () => { Shot("15c_docked_chase"); GD.Print($"[capture] docked at {_stations.DockedAt?.Name ?? "NOTHING"}"); }),
            (t += 0.1f, () => { EnterWalking(); _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot); OnHatch(); }),
            (t += 4.5f, () =>
            {
                Shot("16a_station_airlock");
                GD.Print($"[capture] station ready={_station?.Ready} powered={_station?.Powered} crawlers={_station?.Crawlers.Count} inStation={_player.InStation} player={_player.GlobalPosition}");
            }),
            (t += 0.1f, () => { _player.Flashlight.Visible = true; _player.SetLook(Mathf.Pi * 0.5f, -0.1f); }),
            (t += 0.6f, () => Shot("16b_station_look")),
            (t += 0.1f, () => _player.SetLook(Mathf.Pi, -0.1f)),
            (t += 0.6f, () => Shot("16c_station_look")),
            (t += 0.1f, () =>
            {
                // Walk the player 10 m from the nearest crawler along its path, then make a noise.
                var cr = NearestLiveCrawler();
                if (cr != null)
                {
                    Vector3 meet = _station.PointAlongPath(cr.GlobalPosition, _player.GlobalPosition, 10f);
                    _player.GlobalPosition = meet + Vector3.Up * 0.1f;
                }
                _station?.Noise(_player.GlobalPosition, 200f);
                GD.Print($"[capture] woke {_station?.Crawlers.Count} crawlers; nearest at {NearestCrawlerDistance():0.0} m");
                GD.Print($"[capture] nav: {_station?.NavDiagnostics(NearestLiveCrawler()?.GlobalPosition ?? Vector3.Zero, _player.GlobalPosition)}");
            }),
            (t += 1.2f, () => { Shot("16d_crawler_hunting"); GD.Print($"[capture] after 3 s nearest crawler at {NearestCrawlerDistance():0.0} m: {NearestLiveCrawler()?.Debug}"); }),
            (t += 0.2f, () => { GD.Print($"[capture] then nearest crawler at {NearestCrawlerDistance():0.0} m: {NearestLiveCrawler()?.Debug}"); Shot("16d2_crawler_arrived"); }),
            (t += 0.1f, () => FireOnce()),
            (t += 0.3f, () => { FireOnce(); Shot("16e_firing"); }),
            (t += 0.3f, () => FireOnce()),
            (t += 0.3f, () => FireOnce()),
            (t += 0.3f, () => FireOnce()),
            (t += 0.3f, () => FireOnce()),
            (t += 0.3f, () => FireOnce()),
            (t += 1.2f, () =>
            {
                Shot("16f_after_fight");
                int dead = _station?.Crawlers.FindAll(k => k.Current == Solitude.Creatures.Crawler.State.Dead).Count ?? 0;
                GD.Print($"[capture] crawlers dead={dead}/{_station?.Crawlers.Count} playerHP={_player.Health:0} mag={_weapons.Mag[_weapons.Current]}/{_weapons.Reserve[_weapons.Current]}");
            }),
            (t += 0.1f, () =>
            {
                // Pick up everything within 60 m except the airlock; log one.
                int taken = 0; Interactable logIt = null;
                foreach (var n in _station.GetChildren())
                    if (n is Interactable it && it.OnUse != null && !it.Prompt.StartsWith("Airlock") && !it.Prompt.StartsWith("Bulkhead") && !it.Prompt.StartsWith("Hollow"))
                    {
                        if (it.Prompt == "Read crew log") { logIt ??= it; continue; }
                        it.Use(); taken++;
                    }
                GD.Print($"[capture] loot taken={taken} hold={_systems.Hold.Weight} weapons={string.Join(",", _weapons.Owned)}");
                logIt?.Use();
            }),
            (t += 0.6f, () => Shot("16g_reading_log")),
            (t += 0.1f, () => { CloseLog(); ReturnToSub(); }),
            (t += 2.5f, () => { Shot("17a_back_in_sub"); GD.Print($"[capture] back: inStation={_player.InStation} known stations={CountKnown()}"); }),
            (t += 0.1f, () => { EnterHelm(); FreeStation(); _stations.Undock(); }),
            (t += 0.1f, () => { _interior.Holo.ZoomIndex = 2; EnterWalking(); _player.Position = new Vector3(0.2f, 0.05f, -2.0f); EnterHolo(); }),
            (t += 1.5f, () => Shot("17b_holo_beacons")),
            (t += 0.1f, () => EnterWalking()),
            // --- Milestone 5: suit up, go outside, look back, collect, meet the ravager, come home
            (t += 0.1f, () =>
            {
                _player.Position = new Vector3(-1.4f, 0.05f, 11.4f);
                OnSuit();
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
            }),
            (t += 1.5f, () => { _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot); GD.Print($"[capture] dive attempt: {TryDiveFromHatch()} subSpeed={_sub.LinearVelocity.Length():0.00} engine={_sub.EngineOutput:0.00}"); }),
            (t += 3.0f, () => { Shot("18a_outside_hatch"); GD.Print($"[capture] diving={_diver.Active} mode={_mode} O2={_diver.Oxygen:0} depth={-_diver.GlobalPosition.Y:0}"); }),
            (t += 0.1f, () => { _diver.Place(_diver.GlobalPosition + _sub.GlobalBasis.Z * -6f + Vector3.Up * 2f, Mathf.Atan2(-_sub.GlobalBasis.Z.X, -_sub.GlobalBasis.Z.Z) + Mathf.Pi); }),
            (t += 1.0f, () => Shot("18b_sub_from_outside")),
            (t += 0.1f, () =>
            {
                var pk = _terrain.NearestPickup(_diver.GlobalPosition, 200f);
                if (pk != null)
                {
                    Vector3 from = pk.Position + new Vector3(1.5f, 1.2f, 0f);
                    Vector3 d = pk.Position - from;
                    _diver.Place(from, Mathf.Atan2(-d.X, -d.Z));
                    GD.Print($"[capture] swimming to {pk.Type} at {pk.Position.DistanceTo(_sub.GlobalPosition):0} m from the boat");
                }
            }),
            (t += 0.8f, () => { Shot("18c_pickup"); GD.Print($"[capture] near pickup: {_diver.Nearest?.Type.ToString() ?? "none"}; collect -> {_diver.Collect()?.ToString() ?? "nothing"}; bag={_diver.Bag.Count}"); }),
            (t += 0.1f, () => { _ravager.DiverPass(); }),
            (t += 14.0f, () => { Shot("18d_ravager_vs_diver"); GD.Print($"[capture] ravager to diver {_ravager.DistanceToDiver:0.0} m, heartbeat={_audio.Heartbeat:0.00}, O2={_diver.Oxygen:0}"); }),
            (t += 2.0f, () => Shot("18e_ravager_vs_diver")),
            (t += 0.1f, () => { _diver.Place(_sub.GlobalTransform * HatchLocal + Vector3.Up * 1.5f, 0f); }),
            (t += 0.3f, () => { int before = _systems.Hold.Weight; ExitDive(); GD.Print($"[capture] exit dive from hold weight {before}"); }),
            (t += 2.8f, () => { Shot("18f_back_aboard"); GD.Print($"[capture] back aboard: diving={_diver.Active} mode={_mode} hold={_systems.Hold.Weight} bag={_diver.Bag.Count}"); }),
            (t += 0.1f, () => { OnSuit(); }),
            (t += 0.1f, () => { EnterWalking(); EnterHelm(); var p = _sub.GlobalPosition; _sub.GlobalPosition = new Vector3(p.X, _gen.Height(p.X, p.Z) + 7f, p.Z); _sub.TelegraphIndex = 4; _sub.ApplyTelegraph(); }),
            (t += 1.5f, () => Shot("07_helm_bridge")),
            (t += 0.1f, () => { _view = HelmView.BowCamera; ApplyHelmView(); _ravager.ForcePass(26f, teleport: true); }),
            (t += 1.0f, () => Shot("08_bow_camera")),
            (t += 1.6f, () => Shot("08b_bow_ravager")),
            (t += 1.6f, () => Shot("08c_bow_ravager")),
            (t += 0.1f, () => { _view = HelmView.Bridge; ApplyHelmView(); }),
            (t += 0.6f, () => Shot("08d_bridge_ravager")),
            // Documentary camera: follow the ravager up close to check skinning and swimming.
            (t += 0.1f, () => { _docCam = new Camera3D { Fov = 60f, Far = 200f }; _docCam.AddChild(new SpotLight3D { SpotRange = 60f, SpotAngle = 35f, LightEnergy = 6f }); _game.AddChild(_docCam); _docCam.MakeCurrent(); }),
            (t += 0.8f, () => Shot("12a_ravager_side")),
            (t += 0.7f, () => Shot("12b_ravager_side")),
            (t += 0.1f, () => _docSide = -1f),
            (t += 0.8f, () => Shot("12c_ravager_front")),
            (t += 0.1f, () => { _docSide = 2f; }),
            (t += 0.8f, () => Shot("12d_ravager_top")),
            (t += 0.1f, () => { _docCam.QueueFree(); _docCam = null; _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 0.1f, () => { _view = HelmView.Chase; ApplyHelmView(); }),
            (t += 4.0f, () => Shot("09_chase")),
            (t += 0.1f, () => { _sub.Rudder = 1f; _sub.Planes = -0.4f; }),
            (t += 10.0f, () => Shot("10_chase_turning")),
            (t += 0.1f, () => { _sub.LightsOn = false; }),
            (t += 1.0f, () => Shot("11_lights_off")),
            // --- The whale: a gentle giant passing, filmed from the side, the front, above, and from the boat
            (t += 0.1f, CaptureMarkWhale),
            (t += 0.1f, () =>
            {
                EnterWalking(); EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
                var sp = _sub.GlobalPosition;
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Min(_gen.Height(sp.X, sp.Z) + 60f, -420f), sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true;
                _creatures.SummonWhale();
                // Bring it in close so we don't wait two minutes for it to arrive.
                var w = _creatures.Whale;
                Vector3 d = -w.GlobalBasis.Z;
                w.GlobalPosition = _sub.GlobalPosition + w.GlobalBasis.X * 100f - d * 40f + Vector3.Up * 10f;
                _docTarget = w; _docScale = 2.3f; _docSide = 1f; _docForward = 0f;
                _docCam = new Camera3D { Fov = 60f, Far = 400f };
                _docCam.AddChild(new SpotLight3D { SpotRange = 160f, SpotAngle = 45f, LightEnergy = 10f });
                _game.AddChild(_docCam); _docCam.MakeCurrent();
            }),
            (t += 1.5f, () => { Shot("31a_whale_side"); GD.Print($"[capture] whale at {_creatures.Whale?.DistanceToFocus:0} m, speed-ish, sonar bodies {System.Linq.Enumerable.Count(_creatures.Bodies())}"); }),
            (t += 2.5f, () => Shot("31b_whale_side_later")),
            (t += 0.1f, () => { _docSide = -1f; _docScale = 2.1f; }),
            (t += 1.0f, () => Shot("31c_whale_front")),
            (t += 0.1f, () => { _docSide = 2f; _docScale = 2.2f; }),
            (t += 1.0f, () => Shot("31d_whale_top")),
            (t += 0.1f, () =>
            {
                // From the boat, the F4 way: across the bow, watched on CAM 1 full screen.
                _docCam.QueueFree(); _docCam = null; _docTarget = null;
                Vector3 fwd = -_sub.GlobalBasis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                Vector3 side = new(-fwd.Z, 0, fwd.X);
                Vector3 centre = _sub.GlobalPosition + fwd * 45f + Vector3.Up * 4f;
                _creatures.SummonWhaleBetween(centre - side * 30f, centre + side * 420f);
                EnterWalking(); _player.Position = new Vector3(-0.9f, 0.05f, -3f); EnterMonitors();
                SelectFeed(0); _feedFullscreen = true; _hud.ShowFeed(_rig.Feeds[0].Viewport.GetTexture(), _rig.Feeds[0].Name, 1f);
            }),
            (t += 6.0f, () =>
            {
                Shot("31e_whale_cam1");
                var w = _creatures.Whale;
                GD.Print(w == null ? "[capture] whale: none" : $"[capture] whale relative to boat: {_sub.GlobalTransform.AffineInverse() * w.GlobalPosition} (−Z = ahead), visible={w.IsVisibleInTree()}, leaving={w.Leaving}, scale={w.Scale}");
            }),
            (t += 8.0f, () => { Shot("31f_whale_cam1_later"); EnterWalking(); }),
            // --- The eye at the window: the whale stops with its eye at CAM 2/3
            (t += 0.1f, CaptureMarkEye),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var sp = _sub.GlobalPosition;
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Min(_gen.Height(sp.X, sp.Z) + 60f, -420f), sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true;
                StartEyeEncounter(false);
                _eyeBoatStart = _sub.GlobalPosition;
                _player.Position = new Vector3(-0.9f, 0.05f, -3f); EnterMonitors();
                _feedFullscreen = true; _hud.ShowFeed(_rig.Feeds[_eyeCam].Viewport.GetTexture(), _rig.Feeds[_eyeCam].Name, 1f);
            }),
            (t += 20f, () => { Shot("32a_eye_approach"); LogEye(); }),
            (t += 18f, () => { Shot("32b_eye_arriving"); LogEye(); }),
            (t += 5f, () => { Shot("32c_eye_1"); LogEye(); }),
            (t += 3f, () => { Shot("32d_eye_2"); LogEye(); }),
            (t += 3f, () => { Shot("32e_eye_3"); LogEye(); }),
            (t += 3f, () => { Shot("32f_eye_4"); LogEye(); }),
            (t += 10f, () => { Shot("32g_eye_gone"); LogEye(); EnterWalking(); }),
            // --- The hydrophone: waterfall, the Sounding, recording, a creature, own noise
            (t += 0.1f, CaptureMarkHydrophone),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var sp = _sub.GlobalPosition;
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Min(_gen.Height(sp.X, sp.Z) + 140f, -420f), sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = false;
                _listener.Attention = 0f;
                _creatures.SummonRays();
                _ravager.ForcePass(90f);
                _player.Position = new Vector3(1.2f, 0.05f, -4.9f);
                EnterHydrophone();
            }),
            (t += 20f, () => { Shot("30a_hydrophone_waterfall"); LogHydro("after 20 s"); }),
            (t += 0.1f, () => { _beam = RelBearing(_gen.Origin); _phone.Filter = Hydrophone.Band.Low; LogHydro("beam on the Origin, LOW band"); Input.ActionPress(GameInput.Reload); }),
            (t += 4.4f, () => { Input.ActionRelease(GameInput.Reload); LogHydro("after recording the Sounding"); }),
            (t += 0.2f, () =>
            {
                _phone.Filter = Hydrophone.Band.All;
                var rav = _hydroSources.Find(x => x.Class == SoundClass.Ravager);
                _beam = rav.Level > 0f ? rav.Bearing : _beam;
                LogHydro($"beam on the ravager ({rav.Level:0.00} at {rav.Distance:0} m)");
                Input.ActionPress(GameInput.Reload);
            }),
            (t += 4.4f, () => { Input.ActionRelease(GameInput.Reload); Shot("30b_hydrophone_recorded"); LogHydro("after recording the ravager"); }),
            (t += 0.1f, () => { _sub.TelegraphIndex = 5; _sub.ApplyTelegraph(); _feedFullscreen = true; _hud.ShowFeed(_hydroScope.GetTexture(), "HYDROPHONE  -  PASSIVE", 1f, "[F] close"); }),
            (t += 10f, () => { Shot("30c_hydrophone_own_noise"); LogHydro("full ahead 10 s"); }),
            (t += 0.1f, () =>
            {
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                var json = System.Text.Json.JsonSerializer.Serialize(Capture(), Json);
                var back = System.Text.Json.JsonSerializer.Deserialize<SaveData>(json, Json);
                GD.Print($"[capture] hydrophone save: recordings {back.Recordings.Count} [{string.Join("; ", back.Recordings)}] identified [{string.Join(",", back.Identified)}]");
                _audio.Hydro = false; EnterWalking();
            }),
            // --- The Listener: noise, attention, an answered ping, going dark, both encounter endings
            (t += 0.1f, CaptureMarkListener),
            (t += 0.1f, () =>
            {
                EnterWalking(); EnterHelm();
                // Open water, well clear of the seabed, so collisions don't pollute the noise.
                var sp = _sub.GlobalPosition;
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Min(_gen.Height(sp.X, sp.Z) + 140f, -420f), sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.SetHull(100f);
                _listener.Attention = 0f;
                _sub.LightsOn = true;
                _sub.TelegraphIndex = 5; _sub.ApplyTelegraph();   // AHEAD FULL: loud
            }),
            (t += 5f, () => LogListener("full ahead 5 s")),
            (t += 25f, () => LogListener("full ahead 30 s")),
            (t += 0.1f, () => { _sub.TelegraphIndex = 3; _sub.ApplyTelegraph(); _sub.LightsOn = false; }),
            (t += 10f, () => LogListener("ahead slow, dark, 10 s (silent running?)")),
            (t += 0.1f, () => { _listener.Attention = 45f; EnterSonar(); TryPing(); }),
            (t += 1.5f, () => LogListener("after ping")),
            (t += 4.5f, () => { Shot("29a_listener_answer"); LogListener("answered? phantoms=" + System.Linq.Enumerable.Count(_sonar.Contacts, c => c.Ghost)); }),
            (t += 0.1f, () => { EnterWalking(); EnterHelm(); _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = false; }),
            (t += 15f, () => LogListener("all stop + dark 15 s")),
            // Encounter 1: go silent and it passes.
            (t += 0.1f, () => { _capHull = _sub.Hull; _listener.ForceEncounter(); }),
            (t += 1.0f, () => LogListener("encounter (silent) started")),
            (t += 0.1f, () => { EnterSonar(); }),
            (t += 3.0f, () => Shot("29b_encounter_sonar")),
            (t += 0.1f, () => { EnterWalking(); EnterHelm(); }),
            (t += 20f, () => LogListener($"encounter (silent) over: hull {_capHull:0} -> {_sub.Hull:0}")),
            // Encounter 2: stay loud and it finds you.
            (t += 0.1f, () => { _capHull = _sub.Hull; _listener.ForceEncounter(); _sub.TelegraphIndex = 5; _sub.ApplyTelegraph(); _sub.LightsOn = true; }),
            (t += 23f, () => { Shot("29c_encounter_found"); LogListener($"encounter (loud) over: hull {_capHull:0} -> {_sub.Hull:0}"); }),
            (t += 0.1f, () => { _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _listener.Attention = 0f; _sub.SetHull(100f); }),
            // --- Modules: components, blueprints, the workbench, the rack
            (t += 0.1f, CaptureMarkModules),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var h = _systems.Hold;
                h.Capacity = 999;
                foreach (var (id, n) in new[] { (ItemId.Servo, 3), (ItemId.Valve, 3), (ItemId.Coil, 4), (ItemId.Optics, 1), (ItemId.LogicBoard, 3), (ItemId.Impeller, 2),
                                                 (ItemId.MetalPlate, 10), (ItemId.StationAlloy, 6), (ItemId.Crystal, 6), (ItemId.Sealant, 3), (ItemId.Circuit, 3), (ItemId.PowerCell, 2) })
                    h.Add(id, n);
                _systems.SetState(100f, _systems.Food);
                OnBlueprint(core: true); OnBlueprint(core: true); OnBlueprint(core: false);
                EnterPanel("workbench");
            }),
            (t += 1.0f, () => { Shot("28a_workbench_modules"); GD.Print($"[capture] blueprints: {string.Join(",", _modules.Known)}"); }),
            (t += 0.1f, () =>
            {
                FabricateModule(Modules.Def(ModuleId.HoldRacks));
                FabricateModule(Modules.Def(ModuleId.Thermal));
                FabricateModule(Modules.Def(ModuleId.FrameI));
                FabricateModule(Modules.Def(ModuleId.SuitTank));
                FabricateModule(Modules.Def(ModuleId.Battery));
                EnterWalking();
                EnterPanel("engineering");
                FitModule(Modules.Def(ModuleId.HoldRacks)); FitModule(Modules.Def(ModuleId.Thermal)); FitModule(Modules.Def(ModuleId.FrameI)); FitModule(Modules.Def(ModuleId.SuitTank));
                FitModule(Modules.Def(ModuleId.Battery));   // must refuse: rack full
                RefreshPanel();
            }),
            (t += 1.0f, () => { Shot("28b_engineering_rack"); GD.Print($"[capture] rack {_modules.Used}/{_modules.Bays}: {string.Join(",", _modules.Fitted)}; hold cap {_systems.Hold.Capacity}, vent range {_systems.VentRange}, crush {_sub.CrushDepth}, tank {_diver.TankMinutes}"); }),
            (t += 0.1f, () =>
            {
                RemoveModule(Modules.Def(ModuleId.SuitTank));
                foreach (var id in new[] { ModuleId.FrameI, ModuleId.HoldRacks, ModuleId.Thermal, ModuleId.SuitTank }) RemoveModule(Modules.Def(id));
                GD.Print($"[capture] after removing all: rack {_modules.Used}/{_modules.Bays}, crush {_sub.CrushDepth}, hold cap {_systems.Hold.Capacity}");
                EnterWalking();
            }),
            // --- The diver's harpoon: lag, spear on a tether, the shock prod
            (t += 0.1f, CaptureMarkHarpoon),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _suitOn = false; OnSuit();
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot);
                TryDiveFromHatch();
            }),
            (t += 3.2f, () => { _diver.Place(_diver.GlobalPosition + Vector3.Up * 3f, 0.3f); _diver.Look(new Vector2(0, -260)); }),
            (t += 1.2f, () => { Shot("27a_harpoon_idle"); GD.Print($"[capture] harpoon: {_harpoon.Status} diving={_diver.Active}"); }),
            (t += 0.1f, () => _diver.Look(new Vector2(260, 0))),
            (t += 0.12f, () => Shot("27b_harpoon_lag_turn")),
            (t += 1.2f, () =>
            {
                // Bring the ravager right in front, side-on, and shoot it.
                Vector3 f = -_diver.Camera.GlobalBasis.Z;
                _ravager.GlobalPosition = _diver.Camera.GlobalPosition + f * 14f;
                _ravager.LookAt(_ravager.GlobalPosition + _diver.Camera.GlobalBasis.X, Vector3.Up);
            }),
            (t += 0.6f, () => { Shot("27c_aim_at_ravager"); _harpoon.Fire(); }),
            (t += 0.25f, () => { Shot("27d_spear_flying"); GD.Print($"[capture] spear: {_harpoon.Status}"); }),
            (t += 0.5f, () => { Shot("27e_spear_stuck"); GD.Print($"[capture] after shot: {_harpoon.Status}, ravager to diver {_ravager.DistanceToDiver:0.0} m"); }),
            (t += 1.6f, () => Shot("27f_reeling")),
            (t += 2.5f, () => { GD.Print($"[capture] reeled: {_harpoon.Status}, ravager to diver {_ravager.DistanceToDiver:0.0} m"); _harpoon.Swap(); }),
            (t += 1.0f, () => { Shot("27g_prod"); GD.Print($"[capture] mode {_harpoon.Current}: {_harpoon.Status}"); }),
            (t += 0.1f, () =>
            {
                Vector3 f = -_diver.Camera.GlobalBasis.Z;
                _ravager.GlobalPosition = _diver.Camera.GlobalPosition + f * 2.6f;
                _ravager.LookAt(_ravager.GlobalPosition + _diver.Camera.GlobalBasis.X, Vector3.Up);
                _harpoon.Fire();
            }),
            (t += 0.08f, () => { Shot("27h_zap"); GD.Print($"[capture] zap: stunned={_ravager.Stunned} {_harpoon.Status}"); }),
            (t += 0.1f, () => { _diver.Place(_sub.GlobalTransform * HatchLocal + Vector3.Up * 1.5f, 0f); }),
            (t += 0.3f, () => ExitDive()),
            (t += 2.6f, () => { }),
            // --- BALE IX: the six-lamp proximity panel + pan/tilt/zoom hull cameras
            (t += 0.1f, CaptureMarkProx),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var sp = _sub.GlobalPosition;
                _terrain.GenerateImmediate(sp, 2);
                _sub.GlobalPosition = new Vector3(sp.X, _gen.Height(sp.X, sp.Z) + 9f, sp.Z);   // skimming the bottom: KEEL amber
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true;
                EnterHelm(); _view = HelmView.Bridge; ApplyHelmView();
            }),
            (t += 2.5f, () => { Shot("40a_prox_helm_bottom"); LogProx(); }),
            (t += 0.1f, () =>
            {
                // Something big crossing the bow, left to right: BOW goes red, then STBD.
                Vector3 fwd = -_sub.GlobalBasis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                Vector3 side = new(-fwd.Z, 0, fwd.X);
                Vector3 centre = _sub.GlobalPosition + fwd * 40f + Vector3.Up * 6f;
                _creatures.SummonWhaleBetween(centre - side * 30f, centre + side * 420f);
            }),
            (t += 3.0f, () => { Shot("40b_prox_helm_whale"); LogProx(); }),
            (t += 0.1f, () => { _view = HelmView.Chase; ApplyHelmView(); }),
            (t += 1.5f, () => { Shot("40c_prox_hud_chase"); LogProx(); }),
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(-0.9f, 0.05f, -3f); EnterMonitors(); SelectFeed(0); }),
            (t += 1.2f, () => { Shot("40d_monitor_desk"); LogProx(); }),
            (t += 0.1f, () => { _feedFullscreen = true; Input.ActionPress(GameInput.Right); }),
            (t += 1.5f, () => { Input.ActionRelease(GameInput.Right); Input.ActionPress(GameInput.Forward); }),
            (t += 0.6f, () => { Input.ActionRelease(GameInput.Forward); _rig.ZoomStep(0, 1); _rig.ZoomStep(0, 1); }),
            (t += 1.8f, () => { Shot("40e_cam1_aimed_zoomed"); GD.Print($"[capture] cam1 {_rig.AimText(0)}  servo={_camServo:0.00}"); LogProx(); }),
            (t += 0.1f, () => { _rig.Centre(0); }),
            (t += 1.6f, () => { Shot("40f_cam1_centred"); GD.Print($"[capture] cam1 {_rig.AimText(0)}"); }),
            (t += 0.1f, () => { SelectFeed(5); Input.ActionPress(GameInput.Forward); }),
            (t += 1.5f, () => { Input.ActionRelease(GameInput.Forward); }),
            (t += 0.5f, () => { Shot("40g_mast_manual"); GD.Print($"[capture] mast {_rig.AimText(5)}"); }),
            (t += 0.1f, () => { _feedFullscreen = false; _hud.ShowFeed(null, null, 1f); EnterWalking(); _creatures.DismissWhale(); }),
            (t += 1.0f, () => { }),
            // --- The Gloam: hunts by light. Floods on: it comes. Floods off: it searches, then leaves.
            (t += 0.1f, CaptureMarkGloam),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var sp = _sub.GlobalPosition;
                _terrain.GenerateImmediate(sp, 2);
                _sub.GlobalPosition = new Vector3(sp.X, _gen.Height(sp.X, sp.Z) + 25f, sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true;
                EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
                SummonGloam(110f);
                LogGloam("summoned");
            }),
            (t += 5f, () => { Shot("41a_gloam_drawn"); LogGloam("5s"); }),
            (t += 4f, () => { Shot("41b_gloam_close"); LogGloam("9s"); }),
            (t += 4f, () => { Shot("41c_gloam_after"); LogGloam("13s"); }),
            (t += 4f, () => { LogGloam("17s"); }),
            (t += 0.1f, () => { _sub.LightsOn = false; LogGloam("floods off by hand"); }),
            (t += 10f, () => { Shot("41d_gloam_dark"); LogGloam("dark 10s"); }),
            (t += 20f, () => LogGloam("dark 30s")),
            (t += 25f, () => LogGloam("dark 55s")),
            (t += 0.1f, () =>
            {
                // A good look at it: the doc camera with its own lamp (the Gloam ignores this light).
                _docTarget = _gloam?.Body; _docScale = 0.75f; _docSide = 1f; _docForward = 2f;
                _docCam = new Camera3D { Fov = 60f, Far = 300f };
                _docCam.AddChild(new SpotLight3D { SpotRange = 90f, SpotAngle = 50f, LightEnergy = 14f });
                _game.AddChild(_docCam); _docCam.MakeCurrent();
            }),
            (t += 1.0f, () => Shot("41e_gloam_side")),
            (t += 0.1f, () => { _docSide = -1f; _docScale = 0.4f; _docForward = 6f; }),
            (t += 1.0f, () => Shot("41f_gloam_head")),
            (t += 0.1f, () => { _docSide = 2f; _docScale = 0.8f; _docForward = 0f; }),
            (t += 1.0f, () => Shot("41g_gloam_top")),
            (t += 0.1f, () =>
            {
                _docCam.QueueFree(); _docCam = null; _docTarget = null;
                if (_gloam != null) { _gloam.Hurt(30f, _sub.GlobalPosition, false); LogGloam("speared"); _gloam.Hurt(5f, _sub.GlobalPosition, true); LogGloam("prodded"); }
                _gloam?.QueueFree(); _gloam = null;
                _view = HelmView.Bridge; ApplyHelmView(); EnterWalking();
            }),
            (t += 1.0f, () => { }),
            // --- The ship's log terminal (galley wall) and the carried journal
            (t += 0.1f, CaptureMarkTerminal),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _shipLog.AddNote("Day 31. Hydrologist's log", "Day 31. Sonar shows a return at 400 m that wasn't there yesterday. Large. Bram logged it as a fault. I didn't argue.\n\nDay 33. Something has been at the intake grilles. Scrape marks on the inside of the filter housing. Inside.", "RESEARCH STATION LANTERN-8");
                _shipLog.AddNote("Galley whiteboard", "WHO TOOK THE LAST COFFEE. I am not angry. I am writing this down so the sea knows.", "OUTPOST KESTREL-4");
                _hud.Message("BEACON ACQUIRED: RESEARCH STATION LANTERN-8. Bearing added to the helm.");
                _hud.Message("You can't undock with nobody at the helm.");   // not logged
                _hull.OpenBreach(2);
                _hud.Message("HYDROPHONE: something plated just turned toward the floodlights. FLOODS OFF [L]. Slow down.");
                _hud.Message("THE HADAL DEEP. The dark here has a colour, and it is red.");
                _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-0.3f, 0.05f, 2.5f));
                _player.SetLook(0f, 0.05f);
            }),
            (t += 1.5f, () => { Shot("42a_log_wall"); GD.Print($"[capture] log entries {_shipLog.Entries.Count}: {string.Join(" / ", System.Linq.Enumerable.Select(_shipLog.Entries, e => e.Text))}"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => _player.Focused?.Use()),
            (t += 1.2f, () => Shot("42b_terminal_log")),
            (t += 0.1f, () => _logScreen.SetTab(1)),
            (t += 0.4f, () => Shot("42c_terminal_notes")),
            (t += 0.1f, () => _logScreen.SetTab(2)),
            (t += 0.4f, () => Shot("42d_terminal_goals")),
            (t += 0.1f, () => _logScreen.SetTab(3)),
            (t += 0.4f, () => Shot("42e_terminal_boat")),
            (t += 0.1f, () => { CloseTerminal(); GD.Print($"[capture] after close: mode {_mode}"); }),
            (t += 0.5f, () => OpenJournal()),
            (t += 0.6f, () => Shot("42f_journal_notes")),
            (t += 0.1f, () => _journalScreen.SetTab(1)),
            (t += 0.4f, () => Shot("42g_journal_goals")),
            (t += 0.1f, () => { CloseTerminal(); GD.Print($"[capture] after journal: mode {_mode}"); }),
            (t += 0.5f, () => { }),
            // --- Currents and silt storms
            (t += 0.1f, CaptureMarkSilt),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var sp = _sub.GlobalPosition;
                _terrain.GenerateImmediate(sp, 2);
                _sub.GlobalPosition = new Vector3(sp.X, _gen.Height(sp.X, sp.Z) + 30f, sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true;
                EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
                _siltMark = _sub.GlobalPosition;
                LogSilt("still water start");
            }),
            (t += 12f, () => { LogSilt("after 12 s stopped"); _siltMark = _sub.GlobalPosition; Shot("43a_current_chase"); }),
            (t += 0.1f, () =>
            {
                // Find the strongest current within 3 km and park the boat in it.
                Vector3 best = _sub.GlobalPosition; float bs = 0f;
                for (int x = -15; x <= 15; x++)
                    for (int z = -15; z <= 15; z++)
                    {
                        Vector3 q = _sub.GlobalPosition + new Vector3(x * 100f, 0, z * 100f);
                        float sp = _currents.At(q, _shipLog.Clock).Length();
                        if (sp > bs) { bs = sp; best = q; }
                    }
                GD.Print($"[capture] strongest current within 1.5 km: {bs:0.00} m/s at {best}");
                _terrain.GenerateImmediate(best, 2);
                _sub.GlobalPosition = new Vector3(best.X, _gen.Height(best.X, best.Z) + 30f, best.Z);
                _sub.LinearVelocity = Vector3.Zero;
                _siltMark = _sub.GlobalPosition;
            }),
            (t += 15f, () => { LogSilt("in the river, 15 s stopped"); _siltMark = _sub.GlobalPosition; }),
            (t += 0.1f, () => { StartSiltWarning(); LogSilt("warning"); }),
            (t += 20f, () => { Shot("43b_silt_warning"); LogSilt("warning 20 s"); }),
            (t += 30f, () => { Shot("43c_silt_storm_chase"); LogSilt("storm 15 s"); _siltMark = _sub.GlobalPosition; }),
            (t += 0.1f, () => { _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 1.5f, () => Shot("43d_silt_bowcam")),
            (t += 0.1f, () => { _view = HelmView.Bridge; ApplyHelmView(); }),
            (t += 1.5f, () => { Shot("43e_silt_bridge"); _hud.Message("(capture) helm:" + CurrentHelmText()); }),
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(-0.9f, 0.05f, -3f); EnterMonitors(); SelectFeed(2); _feedFullscreen = true; var f = _rig.Feeds[2]; _hud.ShowFeed(f.Viewport.GetTexture(), f.Name, f.Signal); }),
            (t += 2f, () => Shot("43f_silt_cam3")),
            (t += 0.1f, () => { _feedFullscreen = false; _hud.ShowFeed(null, null, 1f); EnterWalking(); EnterSonar(); _sonar.RangeIndex = 1; TryPing(); }),
            (t += 2.5f, () => { _feedFullscreen = true; _hud.ShowFeed(_sonarScope.GetTexture(), "SONAR  -  ACTIVE", 1f); }),
            (t += 0.5f, () => { Shot("43g_silt_sonar"); LogSilt("storm, after ping"); GD.Print($"[capture] hatch interlock in storm: {(TryOpenHatchForCapture() ? "OPEN (wrong)" : "refused")}"); }),
            (t += 0.1f, () => { _feedFullscreen = false; _hud.ShowFeed(null, null, 1f); EnterWalking(); _siltPhase = SiltPhase.Clearing; _siltPhaseT = 0f; }),
            (t += 40f, () => LogSilt("clearing 40 s")),
            (t += 10f, () => { _siltPhase = SiltPhase.None; LogSilt("done"); }),
            // --- The sample lab
            (t += 0.1f, CaptureMarkLab),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _systems.Hold.Add(ItemId.Lumen, 2); _systems.Hold.Add(ItemId.Sediment); _systems.Hold.Add(ItemId.Sealant); _systems.Hold.Add(ItemId.PlateGloam);
                _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-0.9f, 0.05f, 9.95f));
                _player.SetLook(0f, -0.25f);
            }),
            (t += 1.2f, () => { Shot("44a_lab_bench"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); _player.Focused?.Use(); }),
            (t += 1.0f, () => { Shot("44b_lab_panel"); PickPanel(1); GD.Print($"[capture] lab running: {_lab.Running} left {_lab.Left:0}"); }),
            (t += 1.0f, () => { Shot("44c_lab_running"); _lab.Update(500f, true); GD.Print($"[capture] lab done: {string.Join(",", _lab.Done)} lure={_lab.Has(Discovery.LureFlares)} notes={_shipLog.Notes.Count}"); }),
            (t += 1.0f, () => { Shot("44d_lab_after"); _lab.Start(ItemId.PlateGloam); _systems.Hold.Remove(ItemId.PlateGloam); _lab.Update(999f, true); _lab.Start(ItemId.Sediment); _lab.Update(999f, true);
                GD.Print($"[capture] red floods={_lab.Has(Discovery.RedFloods)} forecast={_lab.Has(Discovery.SiltForecast)} warn={SiltWarnLen} sonar recharge={_sonar.Recharge}"); }),
            (t += 0.1f, () => { CloseLog(); EnterWalking(); OpenJournal(); }),
            (t += 0.8f, () => Shot("44e_journal_fieldnote")),
            (t += 0.1f, () =>
            {
                CloseTerminal();
                _systems.Hold.Add(ItemId.LureFlare);
                var sp = _sub.GlobalPosition;
                _terrain.GenerateImmediate(sp, 2);
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Max(_gen.Height(sp.X, sp.Z) + 25f, sp.Y), sp.Z);
                _sub.LinearVelocity = Vector3.Zero; _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LightsOn = true; _sub.RedFloods = false;
                EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
                SummonGloam(130f); _gloam.Provoke();
                ReleaseLure();
                _sub.LightsOn = false;   // go dark, let the flare do the shouting
            }),
            (t += 3f, () => { Shot("44f_lure_out"); GD.Print($"[capture] lure: gloam {_gloam.Current}, to lure {_gloam.Head.DistanceTo(_lure?.GlobalPosition ?? Vector3.Zero):0} m, to boat {_gloam.Head.DistanceTo(_sub.GlobalPosition):0} m"); }),
            (t += 8f, () => GD.Print($"[capture] lure 11s: gloam {_gloam?.Current}, lure alive {_lure != null}, to boat {_gloam?.Head.DistanceTo(_sub.GlobalPosition):0} m, hull {_sub.Hull:0}")),
            (t += 10f, () => GD.Print($"[capture] lure 21s: gloam {_gloam?.Current}, lure alive {_lure != null}, to boat {_gloam?.Head.DistanceTo(_sub.GlobalPosition):0} m, hull {_sub.Hull:0}")),
            (t += 0.1f, () => { _gloam?.QueueFree(); _gloam = null; _sub.LightsOn = true; _sub.RedFloods = true; GD.Print($"[capture] red floods: gloam would see {GloamBrightness():0.00}"); }),
            (t += 1.5f, () => Shot("44g_red_floods")),
            (t += 0.1f, () => { _sub.RedFloods = false; _view = HelmView.Bridge; ApplyHelmView(); EnterWalking(); }),
            (t += 0.5f, () => { }),
            // --- Wrecks and distress calls
            (t += 0.1f, CaptureMarkWrecks),
            (t += 0.1f, () =>
            {
                EnterWalking();
                var all = _wrecks.Near(_sub.GlobalPosition, 12000f).ToList();
                GD.Print($"[capture] wrecks within 12 km: {all.Count} ({string.Join(", ", all.GroupBy(w => w.Kind).Select(g => $"{g.Key} {g.Count()}"))}), pingers {all.Count(w => w.Pinger)}, wrong {all.Count(w => w.Wrong)}");
                _capWrecks = Enum.GetValues<Solitude.Systems.WreckKind>().Select(k => all.Where(w => w.Kind == k).OrderBy(w => Flatness(w.Position) * 400f + w.Position.DistanceTo(_sub.GlobalPosition)).FirstOrDefault()).Where(w => w != null).ToList();
                // Hearing: park 1.2 km from the nearest pinger.
                var pw = all.Where(w => w.Pinger).OrderBy(w => w.Position.DistanceTo(_sub.GlobalPosition)).First();
                Vector3 at = pw.Position + new Vector3(1200f, 0, 0);
                _terrain.GenerateImmediate(at, 2);
                _sub.GlobalPosition = new Vector3(at.X, _gen.Height(at.X, at.Z) + 30f, at.Z); _sub.LinearVelocity = Vector3.Zero;
                _capPinger = pw;
                EnterHelm(); _view = HelmView.Bridge; ApplyHelmView();
            }),
            (t += 0.5f, () => { _hud.Message("COMMS: a man's voice, tired, calm: \"Still here. Heading zero zero zero. Depth five six eight metres. We're fine. Someone will come down the lane.\"", 8f, immediate: true); }),
            (t += 1.0f, () => Shot("45h_helm_subtitle")),
            (t += 5f, () => GD.Print($"[capture] pinger heard: {_wrecksHeard.Contains(_capPinger.Id)}  helm:{WreckHelmText().Replace("\n", " ")}")),
            (t += 8f, () => { Shot("45a_pinger_heard"); GD.Print($"[capture] voice: lines available {VoiceLines.Any}, playing {_voice?.Playing} ({(_voice?.Stream as Resource)?.ResourcePath}), queued {_voiceQueue.Count}"); }),
            (t += 0.1f, () => { _voiceQueue.Clear(); _voice?.Stop(); bool ok = PlayWrongLoop(true); GD.Print($"[capture] wrong loop queued: {ok}, {_voiceQueue.Count} clips"); }),
            (t += 9f, () => GD.Print($"[capture] wrong loop after 9 s: playing {_voice?.Playing}, left {_voiceQueue.Count}")),
            (t += 0.1f, () => CapWreckShot(0)),
            (t += 2.5f, () => Shot("45b_wreck_0")),
            (t += 0.1f, () => CapWreckShot(1)),
            (t += 2.5f, () => Shot("45c_wreck_1")),
            (t += 0.1f, () => CapWreckShot(2)),
            (t += 2.5f, () => Shot("45d_wreck_2")),
            (t += 0.1f, () => CapWreckShot(3)),
            (t += 2.5f, () => Shot("45e_wreck_3")),
            (t += 0.1f, () =>
            {
                _docCam?.QueueFree(); _docCam = null;
                // Pull the recorder of the first wreck with one (as the diver would).
                var w = _capWrecks.FirstOrDefault(x => x.HasRecorder && _wreckNodes.ContainsKey(x.Id)) ?? _capWrecks.First(x => x.HasRecorder);
                if (!_wreckNodes.ContainsKey(w.Id)) { _terrain.GenerateImmediate(w.Position, 2); SpawnWreck(w); }
                var rec = _terrain.NearestPickup(_wreckNodes[w.Id].built.Recorder + Vector3.Up * 0.2f, 1f);
                GD.Print($"[capture] recorder pickup found: {rec != null} type {rec?.Type}");
                if (rec != null) { _diver.Bag.Add(rec.Type); _terrain.Take(rec); OnWreckSalvage(rec.Id, rec.Type); }
                GD.Print($"[capture] silenced now: {Silenced(w)}, notes {_shipLog.Notes.Count}, bag {_diver.Bag.Count}, salvage left nearby {(_terrain.NearestPickup(w.Position, 30f) != null)}");
                _view = HelmView.Bridge; ApplyHelmView();
            }),
            (t += 0.1f, () => { EnterWalking(); OpenJournal(); }),
            (t += 0.8f, () => Shot("45f_recorder_note")),
            (t += 0.1f, () =>
            {
                CloseTerminal();
                var w = _capWrecks[0];
                Vector3 at = w.Position + new Vector3(60f, 0, 0);
                _sub.GlobalPosition = new Vector3(at.X, _gen.Height(at.X, at.Z) + 25f, at.Z); _sub.LinearVelocity = Vector3.Zero;
                EnterSonar(); _sonar.RangeIndex = 0; TryPing();
            }),
            (t += 1.5f, () => { _feedFullscreen = true; _hud.ShowFeed(_sonarScope.GetTexture(), "SONAR  -  ACTIVE", 1f); }),
            (t += 0.5f, () => { Shot("45g_wreck_sonar"); _feedFullscreen = false; _hud.ShowFeed(null, null, 1f); EnterWalking(); LogProx(); }),
            // --- The boat as a body: wear, symptoms, service points
            (t += 0.1f, CaptureMarkWear),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _wear.Use(Part.IntakeFilter, 70f); _wear.Use(Part.Bearings, 90f); _wear.Use(Part.HullSeals, 65f);
                _wear.Use(Part.Scrubber, 50f); _wear.Use(Part.Electronics, 88f); _wear.Use(Part.Suit, 70f);
                foreach (var d in Wear.All) GD.Print($"[capture] wear {d.Part}: {_wear.Condition(d.Part):0} {_wear.StateOf(d.Part)}");
                GD.Print($"[capture] wear effects: engine limit {_sub.WearLimit}, intake yield {_systems.IntakeYield}, suit air x{_diver.AirFactor}");
                // Engineering, looking aft: the filter on the bulkhead, the bearings on the engine.
                _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi + 0.35f), new Vector3(-0.2f, 0.05f, 10.6f));
                _player.SetLook(0f, -0.25f);
            }),
            (t += 1.5f, () => { UpdateWear(0.01f); GD.Print($"[capture] after a frame: engine limit {_sub.WearLimit}, intake yield {_systems.IntakeYield}, suit air x{_diver.AirFactor}"); Shot("46a_engineering"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => { _player.Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.Pi / 2f), new Vector3(1.3f, 0.05f, 8.75f)); _player.SetLook(0f, 0.05f); }),
            (t += 1.2f, () => { Shot("46b_scrubber"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => { _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-1.2f, 0.05f, 8.6f)); _player.SetLook(0f, -0.55f); }),
            (t += 1.2f, () => { Shot("46c_seals"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => { _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-1.2f, 0.05f, -0.45f)); _player.SetLook(0f, -0.1f); }),
            (t += 1.2f, () => { Shot("46d_bus"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () =>
            {
                Service(Part.Bearings);   // nothing in the hold: refused
                _systems.Hold.Add(ItemId.Plankton, 2); _systems.Hold.Add(ItemId.Sealant); _systems.Hold.Add(ItemId.Circuit);
                Service(Part.Bearings); Service(Part.HullSeals); Service(Part.Electronics); Service(Part.IntakeFilter);
                UpdateWear(0.01f);
                foreach (var d in Wear.All) GD.Print($"[capture] after service {d.Part}: {_wear.Condition(d.Part):0} {_wear.StateOf(d.Part)}");
                GD.Print($"[capture] engine limit {_sub.WearLimit}");
                EnterTerminal();
            }),
            (t += 1.0f, () => _logScreen.SetTab(3)),
            (t += 0.5f, () => { Shot("46e_boat_page"); _logScreen.SetTab(0); }),
            (t += 0.4f, () => { Shot("46f_log_page"); CloseTerminal(); }),
            (t += 0.5f, () => { }),
            // --- Someone alive out there (Hanna), on fast timers
            (t += 0.1f, CaptureMarkHanna),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _stations.StateOf(_stations.Directory.Starter).Visited = true;
                var sp = _sub.GlobalPosition;
                _sub.GlobalPosition = new Vector3(sp.X, Mathf.Min(sp.Y, _gen.Height(sp.X, sp.Z) + 30f), sp.Z);
                _hannaT = 0.5f;
                _voiceQueue.Clear(); _voice?.Stop(); _wreckHearT = 9999f; _loopReplayT = 9999f;   // a quiet radio for this test
                LogHanna("start");
            }),
            (t += 2f, () => LogHanna("after first call")),
            (t += 9f, () => { _systems.UsePower(-50f); TryPing(); }),
            (t += 4.6f, () => { TryPing(); }),
            (t += 4f, () => { LogHanna("after two pings"); }),
            (t += 25f, () => { LogHanna("her lines played"); EnterTerminal(); }),
            (t += 1f, () => _logScreen.SetTab(2)),
            (t += 0.5f, () => { Shot("47a_goals_hanna"); _logScreen.SetTab(1); }),
            (t += 0.5f, () => { Shot("47b_notes_hanna"); CloseTerminal(); }),
            (t += 0.3f, () =>
            {
                // Dock at her station and step through to the supply lock.
                var site = HannaSite;
                _stations.ForceDock(site);
                _player.Position = new Vector3(-0.9f, 0.05f, 12.4f);
                OnHatch();
            }),
            (t += 5f, () => { LogHanna("in her station"); var it = _station?.GetNodeOrNull("SupplyLock"); GD.Print($"[capture] supply lock present: {it != null}"); }),
            (t += 0.1f, () => { if (_station?.GetNodeOrNull<Node3D>("SupplyLock") is { } lk) { _player.LookAt(lk.GlobalPosition, Vector3.Up); } }),
            (t += 6f, () => { Shot("47c_supply_lock"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => { UseSupplyLock(); _systems.Hold.Add(ItemId.Crystal, 2); _systems.Hold.Add(ItemId.PowerCell); UseSupplyLock(); LogHanna("delivered"); }),
            (t += 16f, () => { UseSupplyLock(); LogHanna("gift"); _hannaT = 0.1f; }),
            (t += 14f, () => { LogHanna("sister asked"); _hannaT = 0.1f; }),
            (t += 16f, () => { LogHanna("ascent asked"); _systems.Hold.Add(ItemId.PowerCell); UseSupplyLock(); }),
            (t += 12f, () => { LogHanna("end"); GD.Print($"[capture] hanna log lines: {_shipLog.Entries.Count(e => e.Text.StartsWith("Hanna"))}"); }),
            (t += 0.5f, () => { ReturnToSub(); }),
            (t += 2f, () => { }),
            // --- Voiced station logs: the crew
            (t += 0.1f, CaptureMarkCrew),
            (t += 6f, () => { _voiceQueue.Clear(); _voice?.Stop(); }),
            (t += 0.1f, () =>
            {
                EnterWalking(); _voiceQueue.Clear(); _voice?.Stop(); _wreckHearT = 9999f; _loopReplayT = 9999f;
                int stations = 0, voiced = 0; var who = new HashSet<string>(); Solitude.Stations.StationLogs.Log sample = null;
                foreach (var site in _stations.Directory.Near(_sub.GlobalPosition, 9000f))
                {
                    stations++;
                    foreach (var lg in Solitude.Stations.StationLogs.Build(site.Name, site.Seed, 4))
                        if (lg.VoiceId != null) { voiced++; who.Add(lg.VoiceId.Split('_')[0]); sample ??= lg; }
                }
                GD.Print($"[capture] crew: {voiced} voiced logs across {stations} stations, {who.Count} different people; sample '{sample?.Title}'");
                _wrecksHeard.UnionWith(System.Linq.Enumerable.Select(_wrecks.Near(_sub.GlobalPosition, 3000f), w => w.Id));   // no new pingers mid-test
                if (sample != null) ShowLog(sample);
                GD.Print($"[capture] crew log queued: {_logVoice}, queue {_voiceQueue.Count}, line {VoiceLines.Get(sample?.VoiceId ?? "")?.File}, loads {GD.Load<AudioStream>(VoiceLines.Get(sample?.VoiceId ?? "")?.File ?? "res://x") != null}");
            }),
            (t += 2.5f, () => { Shot("48a_voiced_log"); GD.Print($"[capture] crew log playing: {_voice?.Playing} ({(_voice?.Stream as Resource)?.ResourcePath})"); CloseLog(); }),
            (t += 0.3f, () => GD.Print($"[capture] after putting it down: playing {_voice?.Playing}")),
            // --- The suit's bubbles: still, then swimming, then still again
            (t += 0.1f, CaptureMarkSwim),
            (t += 0.1f, () =>
            {
                EnterWalking();
                _suitOn = false; OnSuit();
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                _player.Transform = new Transform3D(Basis.Identity, SubHatchSpot);
                TryDiveFromHatch();
            }),
            (t += 3.2f, () => { _diver.Place(_diver.GlobalPosition + Vector3.Up * 4f + _sub.GlobalBasis.X * 8f, 0f); _swimLog = true; GD.Print("[swim] holding still"); }),
            (t += 6f, () => { Input.ActionPress(GameInput.Forward); GD.Print("[swim] swimming"); }),
            (t += 8f, () => { Input.ActionRelease(GameInput.Forward); GD.Print("[swim] stopped"); }),
            (t += 5f, () => { _swimLog = false; }),
            // --- The hold's manifest (I)
            (t += 0.1f, CaptureMarkHold),
            (t += 0.1f, () =>
            {
                EnterWalking(); _voiceQueue.Clear(); _voice?.Stop(); _wreckHearT = 9999f; _loopReplayT = 9999f;
                var h = _systems.Hold;
                h.Add(ItemId.Crystal, 3); h.Add(ItemId.Lumen, 2); h.Add(ItemId.PlateGloam); h.Add(ItemId.Servo, 2); h.Add(ItemId.StationAlloy);
                h.Add(ItemId.CannedFood, 2); h.Add(ItemId.ClamStew); h.Add(ItemId.ModBilge);
                GD.Print($"[hold] weight {h.Weight}/{h.Capacity}");
                OpenHold();
            }),
            (t += 0.8f, () => { Shot("49a_hold_samples"); _holdScreen.Step(1); }),
            (t += 0.4f, () => { Shot("49b_hold_samples_2"); _holdScreen.SetTab(1); }),
            (t += 0.4f, () => { Shot("49c_hold_salvage"); _holdScreen.SetTab(3); }),
            (t += 0.4f, () =>
            {
                Shot("49d_hold_food");
                float before = _systems.Food; int cans = _systems.Hold.Count(ItemId.CannedFood);
                var items = HoldItems((HoldGroup)_holdScreen.Current);
                _systems.Eat(items[_holdScreen.Selected]);
                GD.Print($"[hold] ate {items[0]}: food {before:0} -> {_systems.Food:0}, cans {cans} -> {_systems.Hold.Count(ItemId.CannedFood)}");
                _holdScreen.SetTab(4);
            }),
            (t += 0.4f, () => { Shot("49e_hold_modules"); CloseTerminal(); GD.Print($"[hold] closed: mode {_mode}"); }),
            (t += 0.3f, () => { _player.InStation = true; OpenHold(); }),
            (t += 0.8f, () => { Shot("49f_hold_paper"); CloseTerminal(); _player.InStation = false; }),
            (t += 0.3f, () => { }),
            // --- Tom's quick tests: turn rate, the locker model
            (t += 0.1f, CaptureMarkTest),
            (t += 0.1f, () => { EnterWalking(); _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(-0.2f, 0.05f, 11.6f)); /* face port (-X) */ _player.SetLook(0f, -0.1f); }),
            (t += 1.2f, () => { Shot("22a_locker"); GD.Print($"[capture] focused: {_player.Focused?.Prompt ?? "nothing"}"); }),
            (t += 0.1f, () => { _player.Focused?.Use(); GD.Print($"[capture] suit on: {_suitOn}"); }),
            (t += 0.1f, () =>
            {
                EnterHelm();
                _sub.TelegraphIndex = 5; _sub.ApplyTelegraph(); _sub.Rudder = 0f;
                _captureHeading0 = -1f;
            }),
            (t += 14.0f, () => { _sub.Rudder = 1f; _captureHeading0 = _sub.Heading; GD.Print($"[capture] full ahead {_sub.ForwardSpeed:0.0} m/s, full right rudder"); }),
            (t += 10.0f, () => GD.Print($"[capture] turned {Mathf.PosMod(_sub.Heading - _captureHeading0, 360f):0}° in 10 s, yaw rate {Mathf.Abs(_sub.YawRate):0.0}°/s, roll {_sub.RollDeg:0.0}°")),
            (t += 0.1f, () => { _sub.TelegraphIndex = 2; _sub.ApplyTelegraph(); _sub.LinearVelocity = Vector3.Zero; _sub.Rudder = 1f; _captureHeading0 = _sub.Heading; }),
            (t += 10.0f, () => GD.Print($"[capture] at a stop (bow thrusters): {Mathf.PosMod(_sub.Heading - _captureHeading0, 360f):0}° in 10 s")),
            // Jaws: side close-ups, forced closed then open, for the ravager and the Fangwyrm.
            (t += 0.1f, () =>
            {
                _ravager.ForcePass(26f, teleport: true);
                _docTarget = _ravager; _docScale = 0.3f; _docSide = 1f; _docForward = 5.5f;
                _docCam = new Camera3D { Fov = 55f, Far = 200f };
                _docCam.AddChild(new SpotLight3D { SpotRange = 60f, SpotAngle = 45f, LightEnergy = 10f });
                _game.AddChild(_docCam); _docCam.MakeCurrent();
                _ravager.JawOverride = Solitude.Creatures.Ravager.JawClosedAngle;
            }),
            (t += 0.8f, () => Shot("23a_ravager_jaw_closed")),
            (t += 0.1f, () => _ravager.JawOverride = Solitude.Creatures.Ravager.JawOpenAngle),
            (t += 0.4f, () => Shot("23b_ravager_jaw_open")),
            (t += 0.1f, () =>
            {
                _ravager.JawOverride = null;
                _creatures.SummonWyrm(); _docTarget = _creatures.Wyrm; _docScale = 0.45f; _docSide = 1f; _docForward = 16f;
                _creatures.Wyrm.JawOverride = Solitude.Creatures.CreatureProfile.Fangwyrm().JawClosed;
            }),
            (t += 0.8f, () => Shot("23c_wyrm_jaw_closed")),
            (t += 0.1f, () => _creatures.Wyrm.JawOverride = Solitude.Creatures.CreatureProfile.Fangwyrm().JawOpen),
            (t += 0.4f, () => Shot("23d_wyrm_jaw_open")),
            (t += 0.1f, () => { _creatures.Wyrm.JawOverride = null; _docCam.QueueFree(); _docCam = null; _docTarget = null; _docForward = 0f; }),
            // Pause menu (the world is frozen, so shots run on a pause-proof timer).
            (t += 0.1f, () =>
            {
                EnterWalking();
                OpenPause();
                var tree = GetTree();
                tree.CreateTimer(0.6, processAlways: true).Timeout += () => { Shot("25a_pause"); GD.Print($"[capture] paused={tree.Paused} menu={_pause.Visible}"); };
                tree.CreateTimer(0.7, processAlways: true).Timeout += () => _pause.ShowSettings(true);
                tree.CreateTimer(1.3, processAlways: true).Timeout += () => Shot("25b_pause_settings");
                tree.CreateTimer(1.4, processAlways: true).Timeout += () => { ClosePause(); GD.Print($"[capture] resumed: paused={tree.Paused}"); };
            }),
            (t += 0.2f, () => { }),
            // Hull events: a breach in the galley, flooding, steam, drips, a real weld, knocks.
            (t += 0.1f, () =>
            {
                EnterWalking();
                _sub.SetHull(45f);                       // damaged: drips start
                _systems.Hold.Add(ItemId.Sealant, 1);
                _systems.Hold.Add(ItemId.MultiTool, 1);
                _hull.OpenBreach(2, 1f);
                var b = _hull.FirstBreach.Value;
                // Stand 1.4 m in front of the breach, looking at it.
                Vector3 stand = b.pos + b.normal * 1.4f; stand.Y = 0.05f;
                _player.Position = stand;
                _player.Transform = new Transform3D(Basis.LookingAt(-b.normal, Vector3.Up), stand);
                _player.SetLook(0f, -0.25f);
                GD.Print($"[capture] breach in {HullEvents.RoomNames[b.room]} at {b.pos}");
            }),
            (t += 1.5f, () => Shot("26a_breach")),
            (t += 9.8f, () =>
            {
                _player.SetLook(0.5f, -0.85f);
                GD.Print($"[capture] levels after 10 s: {string.Join(" | ", System.Linq.Enumerable.Range(0, 5).Select(i => $"{HullEvents.RoomNames[i]} {_hull.Level(i):0.000}"))}");
            }),
            (t += 0.2f, () => { Shot("26b_flooding"); GD.Print($"[capture] galley water {_hull.Level(2):0.00} m, flood weight {_sub.FloodWeight:0}, player water factor {_player.WaterFactor:0.00}, warning '{_hull.Warning()}'"); }),
            (t += 0.1f, () => { _hull.Vent(new Vector3(1.6f, 2.42f, 2.6f)); _player.SetLook(0.4f, 0.35f); }),
            (t += 0.5f, () => Shot("26c_steam")),
            (t += 0.1f, () => { _player.SetLook(0f, -0.25f); Input.ActionPress(GameInput.Interact); }),
            (t += 1.2f, () => Shot("26c2_welding")),
            (t += 1.8f, () => { Input.ActionRelease(GameInput.Interact); GD.Print($"[capture] after welding: breaches={_hull.ActiveBreaches} sealant={_systems.Hold.Count(ItemId.Sealant)} focused='{_player.Focused?.Prompt}'"); }),
            (t += 0.1f, () =>
            {
                // Side view down the length of the boat, through the doorways, to see one continuous surface.
                _player.Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.05f, 8.5f));
                _player.SetLook(0f, -0.35f);
            }),
            (t += 0.6f, () => Shot("26c3_through_doorways")),
            (t += 8.0f, () => { Shot("26d_draining"); GD.Print($"[capture] galley water after pumping {_hull.Level(2):0.00} m"); }),
            (t += 0.1f, () => _hull.Knock()),
            (t += 3.0f, () => { _sub.SetHull(100f); }),
            // Holo table: the red own-ship dot.
            (t += 0.1f, () => { EnterWalking(); _player.Position = new Vector3(0.2f, 0.05f, -2.0f); EnterHolo(); _holoPitch = 0.9f; TryPing(); }),
            (t += 2.5f, () => Shot("24_holo_red_dot")),
            (t += 0.1f, () => EnterWalking()),
            // --- Milestone 6: biomes
            (t += 0.1f, CaptureMarkBiomes),
            (t += 0.1f, () => CaptureGoToBiome(Solitude.World.Biome.Crystal)),
            (t += 4.0f, () => { Shot("20a_crystal_chase"); GD.Print($"[capture] biome here: {_gen.BiomeAt(_sub.GlobalPosition.X, _sub.GlobalPosition.Z)}"); }),
            (t += 0.1f, () => { _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 1.0f, () => Shot("20b_crystal_bow")),
            (t += 0.1f, () => CaptureGoToBiome(Solitude.World.Biome.Hadal)),
            (t += 5.0f, () => { Shot("20c_hadal_chase"); GD.Print($"[capture] biome here: {_gen.BiomeAt(_sub.GlobalPosition.X, _sub.GlobalPosition.Z)} depth={_sub.Depth:0}"); }),
            (t += 0.1f, () => { _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 1.0f, () => Shot("20d_hadal_bow")),
            // --- Tom's new creatures: Crystal Rays and the Fangwyrm
            (t += 0.1f, CaptureMarkCreatures),
            (t += 0.1f, () => { _sub.LightsOn = true; EnterWalking(); EnterHelm(); _view = HelmView.Chase; ApplyHelmView(); _creatures.SummonRays(); }),
            (t += 4.0f, () =>
            {
                _docTarget = _creatures.Rays.Count > 0 ? _creatures.Rays[0] : null;
                _docScale = 0.6f; _docSide = 1f;
                _docCam = new Camera3D { Fov = 60f, Far = 200f };
                _docCam.AddChild(new SpotLight3D { SpotRange = 60f, SpotAngle = 35f, LightEnergy = 4f });
                _game.AddChild(_docCam); _docCam.MakeCurrent();
                GD.Print($"[capture] rays: {_creatures.Rays.Count}");
            }),
            (t += 0.8f, () => Shot("19a_ray_side")),
            (t += 0.9f, () => Shot("19b_ray_side_later")),
            (t += 0.1f, () => _docSide = 2f),
            (t += 0.8f, () => Shot("19c_ray_top")),
            (t += 0.1f, () => _docSide = -1f),
            (t += 0.8f, () => Shot("19d_ray_front")),
            (t += 0.1f, () =>
            {
                _creatures.SummonWyrm(); _docTarget = _creatures.Wyrm; _docScale = 1.1f; _docSide = 1f;
                foreach (var c in _docCam.GetChildren()) if (c is SpotLight3D sl) { sl.LightEnergy = 9f; sl.SpotRange = 80f; sl.SpotAngle = 45f; }
            }),
            (t += 1.0f, () => Shot("19e_wyrm_side")),
            (t += 1.2f, () => Shot("19f_wyrm_side_later")),
            (t += 0.1f, () =>
            {
                _docSide = 2f; _docScale = 1.15f;
                foreach (var c in _docCam.GetChildren()) if (c is SpotLight3D sl) { sl.LightEnergy = 16f; sl.SpotAngle = 60f; }
            }),
            (t += 0.8f, () => Shot("19g1_wyrm_top")),
            (t += 0.5f, () => Shot("19g2_wyrm_top")),
            (t += 0.5f, () => Shot("19g3_wyrm_top")),
            (t += 0.1f, () => { _docSide = -1f; _docScale = 1.1f; }),
            (t += 0.8f, () => Shot("19g_wyrm_front")),
            (t += 0.1f, () => { _docCam.QueueFree(); _docCam = null; _docTarget = null; _view = HelmView.BowCamera; ApplyHelmView(); }),
            (t += 3.0f, () => { Shot("19h_wyrm_from_bow"); GD.Print($"[capture] wyrm distance to boat {_creatures.Wyrm?.DistanceToFocus:0} m"); }),
            // --- Milestone 6: upgrades, data cores, save round-trip, the pit, the Choir, the ending
            (t += 0.1f, CaptureMarkStory),
            (t += 0.1f, () =>
            {
                GD.Print($"[capture] origin at {_gen.Origin} rim depth {-_gen.Origin.Y:0} m, shaft floor {-_gen.OriginFloor:0} m, {new Vector2(_gen.Origin.X - _spawnPoint.X, _gen.Origin.Z - _spawnPoint.Z).Length():0} m from spawn");
                var h = _systems.Hold;
                h.Capacity = 999;
                h.Add(ItemId.MetalPlate, 8); h.Add(ItemId.StationAlloy, 6); h.Add(ItemId.Valve, 3); h.Add(ItemId.Circuit, 2);
                _systems.SetState(100f, _systems.Food);
                FabricateModule(Modules.Def(ModuleId.FrameI));
                FitModule(Modules.Def(ModuleId.FrameI));
                OnBlueprint(core: true);                          // first core blueprint: the Mk II frame
                FabricateModule(Modules.Def(ModuleId.FrameII));
                FitModule(Modules.Def(ModuleId.FrameII));         // swaps Mk I back into the hold
                GD.Print($"[capture] modules={string.Join(",", _modules.Fitted)} bays={_modules.Used}/{_modules.Bays} frameI in hold={h.Count(ItemId.ModFrameI)} test={_sub.TestDepth} crush={_sub.CrushDepth}");
                for (int i = 0; i < 3; i++) OnDataCore(new Solitude.Stations.StationSite { Id = -100 - i, Name = "TEST" });
                GD.Print($"[capture] cores={_coreSites.Count} originKnown={_originKnown}");
                // Save round-trip (in memory: capture runs never touch the real save file).
                var json = System.Text.Json.JsonSerializer.Serialize(Capture(), Json);
                var back = System.Text.Json.JsonSerializer.Deserialize<SaveData>(json, Json);
                GD.Print($"[capture] save {json.Length} bytes; round-trip seed={back.Seed} modules={back.Modules.Count} blueprints={back.Blueprints.Count} cores={back.CoreSites.Count} hold items={back.Hold.Count} taken={back.TakenPickups.Count} stations={back.Stations.Count}");
            }),
            (t += 0.1f, () =>
            {
                // On the rim, looking down into the shaft.
                Vector3 rim = _gen.Origin + new Vector3(Solitude.World.TerrainGenerator.PitRadius * 1.6f, 25f, 0f);
                _terrain.GenerateImmediate(rim, 2);
                _sub.Freeze = false;
                _sub.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), rim);
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                EnterWalking(); EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
            }),
            (t += 4.0f, () => Shot("21a_pit_rim")),
            (t += 0.1f, () =>
            {
                // Inside the shaft, 70 m above the floor, facing the heart.
                Vector3 p = new(_gen.Origin.X + 58f, _gen.OriginFloor + 30f, _gen.Origin.Z);
                _terrain.GenerateImmediate(p, 2);
                Vector3 toHeart = (ChoirHeart - p); toHeart.Y = 0;
                _sub.GlobalTransform = new Transform3D(Basis.LookingAt(toHeart.Normalized(), Vector3.Up), p);
                _sub.LinearVelocity = Vector3.Zero;
                _view = HelmView.BowCamera; ApplyHelmView();
            }),
            (t += 3.0f, () => { Shot("21b_choir_bow"); GD.Print($"[capture] in the shaft at depth {_sub.Depth:0} m, hull {_sub.Hull:0}%, heart {_sub.GlobalPosition.DistanceTo(ChoirHeart):0} m away"); }),
            (t += 0.1f, () => { _view = HelmView.Chase; ApplyHelmView(); }),
            (t += 2.0f, () => Shot("21c_choir_chase")),
            (t += 0.1f, () => { _sub.GlobalPosition = ChoirHeart + new Vector3(40f, 4f, 0f); _sub.LinearVelocity = Vector3.Zero; }),
            (t += 1.0f, () => GD.Print($"[capture] ending running={_endingRunning}")),
            (t += 11.0f, () => { Shot("21d_epilogue"); GD.Print($"[capture] ended={_ended} mode={_mode}"); }),
            (t += 0.5f, () => { _audio.SaveDebugRecording(_captureDir); GetTree().Quit(); }),
        };
    }

    private void FireOnce()
    {
        var exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
        // Aim at the first live crawler, then pull the trigger.
        var c = NearestLiveCrawler();
        if (c != null)
        {
            // SetLook is relative to the player body, which is already turned to the airlock's facing.
            Vector3 to = _player.GlobalBasis.Inverse() * (c.GlobalPosition + Vector3.Up * 0.5f - _player.Camera.GlobalPosition);
            _player.SetLook(Mathf.Atan2(-to.X, -to.Z), Mathf.Asin(Mathf.Clamp(to.Normalized().Y, -1f, 1f)));
        }
        var cam = _player.Camera;
        var probe = _player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, cam.GlobalPosition - cam.GlobalBasis.Z * 60f, Layers.Interior | Layers.Enemy, exclude));
        GD.Print($"[capture] fire probe: hit={(probe.Count > 0 ? ((Node)probe["collider"].AsGodotObject()).Name + " " + probe["collider"].AsGodotObject().GetType().Name + " at " + ((Vector3)probe["position"]).DistanceTo(cam.GlobalPosition).ToString("0.0") + " m" : "nothing")} crawlerAt={(c != null ? c.GlobalPosition.DistanceTo(cam.GlobalPosition).ToString("0.0") : "-")} m");
        _weapons.Trigger(true, true, _player.Camera, _player.GetWorld3D().DirectSpaceState, exclude);
    }

    private Solitude.Creatures.Crawler NearestLiveCrawler()
    {
        Solitude.Creatures.Crawler best = null;
        float bd = float.MaxValue;
        if (_station != null)
            foreach (var k in _station.Crawlers)
            {
                if (k.Current == Solitude.Creatures.Crawler.State.Dead) continue;
                float d = k.GlobalPosition.DistanceTo(_player.GlobalPosition);
                if (d < bd) { bd = d; best = k; }
            }
        return best;
    }

    private float NearestCrawlerDistance() => NearestLiveCrawler()?.GlobalPosition.DistanceTo(_player.GlobalPosition) ?? -1f;

    private int CountKnown()
    {
        int n = 0;
        foreach (var s in _stations.Directory.Near(_sub.GlobalPosition, 4000f)) if (s.Known) n++;
        return n;
    }

    /// <summary>
    /// A section mark. `-- --capture-until=listener` stops when that section begins, saving the
    /// audio dump so far (handy with --capture-from to record one section's sound).
    /// </summary>
    private void CaptureSection(string name)
    {
        GD.Print($"[capture] --- {name} section ---");
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg == $"--capture-until={name}") { _audio.SaveDebugRecording(_captureDir); GD.Print("[capture] stopped (capture-until)"); GetTree().Quit(); }
    }

    private void CaptureMarkCreatures() => CaptureSection("creatures");
    private void CaptureMarkBiomes() => CaptureSection("biomes");
    private float _capHull;
    private void LogListener(string what) =>
        GD.Print($"[capture] listener {what}: attention {_listener.Attention:0.0} noise {_listener.Noise:0.00} ({NoiseDb:0} dB) tier {_listener.Current} encounter {(_listener.InEncounter ? _listener.EncounterT.ToString("0.0") : "-")} depth {_sub.Depth:0}");
    private void LogHydro(string what)
    {
        var (src, level) = InBeam();
        GD.Print($"[capture] hydrophone {what}: beam {_beam:0} band {_phone.Filter} level {level:0.00} in beam {(src is HydroSource s ? $"{s.Class} {s.Level:0.00} @ {s.Distance:0} m" : "nothing")} | sources {_hydroSources.Count}: {string.Join(", ", _hydroSources.ConvertAll(x => $"{x.Class}:{x.Level:0.00}@{x.Bearing:0}"))} | lines: {_hydroDisplay.Line2} / {_hydroDisplay.Line3} | recs {_phone.Recordings.Count} noise {_listener.Noise:0.00}");
    }
    private void CaptureMarkWhale() => CaptureSection("whale");
    private void CaptureMarkEye() => CaptureSection("eye");
    private Vector3 _eyeBoatStart;
    private void LogEye()
    {
        var w = _creatures.Whale;
        if (w == null) { GD.Print("[capture] eye: no whale"); return; }
        Vector3 lens = LensWorld(_eyeCam);
        float d = Mathf.Min(w.EyeWorld(-1).DistanceTo(lens), w.EyeWorld(1).DistanceTo(lens));
        GD.Print($"[capture] eye: phase {_eyePhase} active {_eyeActive} eye-to-lens {d:0.0} m speed {w.SpeedOverride:0.00} touching {_whaleTouching} boat moved {_sub.GlobalPosition.DistanceTo(_eyeBoatStart):0.0} m v {_sub.LinearVelocity} heading {_sub.Heading:0} angvel {_sub.AngularVelocity.Length():0.000} whale {_sub.GlobalTransform.AffineInverse() * w.GlobalPosition}");
    }
    private void CaptureMarkHydrophone() => CaptureSection("hydrophone");
    private void CaptureMarkListener() => CaptureSection("listener");
    private void CaptureMarkModules() => CaptureSection("modules");
    private void CaptureMarkHarpoon() => CaptureSection("harpoon");
    private void CaptureMarkTest() => CaptureSection("test");
    private void CaptureMarkProx() => CaptureSection("prox");
    private void CaptureMarkGloam() => CaptureSection("gloam");
    private void CaptureMarkTerminal() => CaptureSection("terminal");
    private void CaptureMarkSilt() => CaptureSection("silt");
    private void CaptureMarkLab() => CaptureSection("lab");
    private void CaptureMarkWrecks() => CaptureSection("wrecks");
    private void CaptureMarkWear() => CaptureSection("wear");
    private void CaptureMarkHanna() => CaptureSection("hanna");
    private void CaptureMarkSwim() => CaptureSection("swim");
    private void CaptureMarkHold() => CaptureSection("hold");
    private bool _swimLog;
    private float _swimLogT;
    private void CaptureMarkCrew() => CaptureSection("crew");
    private void LogHanna(string what) => GD.Print($"[capture] hanna {what}: state {_hanna}, station {HannaSite?.Name ?? "-"}, voice busy {VoiceBusy}, queued {_voiceQueue.Count}, transcript {_hannaTranscript.Count}");
    private List<Solitude.Systems.WreckSite> _capWrecks = new();
    private Solitude.Systems.WreckSite _capPinger;
    private float Flatness(Vector3 p) => Mathf.Abs(_gen.Height(p.X + 20f, p.Z) - _gen.Height(p.X - 20f, p.Z)) + Mathf.Abs(_gen.Height(p.X, p.Z + 20f) - _gen.Height(p.X, p.Z - 20f));
    private void CapWreckShot(int i)
    {
        if (i >= _capWrecks.Count) return;
        var w = _capWrecks[i];
        _terrain.GenerateImmediate(w.Position, 2);
        // The boat waits nearby (wrecks stream in around the focus).
        _sub.GlobalPosition = w.Position + new Vector3(45f, 30f, 0); _sub.LinearVelocity = Vector3.Zero;
        if (!_wreckNodes.ContainsKey(w.Id)) SpawnWreck(w);
        _docCam ??= new Camera3D { Fov = 62f, Far = 200f };
        if (!_docCam.IsInsideTree()) { _docCam.AddChild(new SpotLight3D { SpotRange = 70f, SpotAngle = 45f, LightEnergy = 9f }); _game.AddChild(_docCam); }
        // Frame it from the side with the doc camera's lamp (it follows _docTarget each frame).
        var mark = new Node3D();
        _game.AddChild(mark);
        mark.GlobalTransform = new Transform3D(new Basis(Vector3.Up, w.Yaw + 0.6f), w.Position + Vector3.Up * 1.5f);
        _docTarget = mark; _docSide = 1f; _docForward = 0f;
        _docScale = w.Kind is Solitude.Systems.WreckKind.SupplySub or Solitude.Systems.WreckKind.CargoString ? 0.95f : 0.45f;
        _docCam.MakeCurrent();
        GD.Print($"[capture] wreck {i}: {w.Name} ({w.Kind}) depth {-w.Position.Y:0} pinger {w.Pinger} wrong {w.Wrong}");
    }
    private Vector3 _siltMark;
    private void LogSilt(string what)
    {
        Vector3 moved = _sub.GlobalPosition - _siltMark; moved.Y = 0;
        GD.Print($"[capture] silt {what}: phase {_siltPhase} silt {_silt:0.00} water {_sub.Water.Length():0.00} m/s set {Solitude.Systems.Currents.SetDegrees(_sub.Water):0} ground {_sub.LinearVelocity.Length():0.00} m/s moved {moved.Length():0.0} m, sonar range {_sonar.PingRange:0}, gloam sees {GloamBrightness():0.00}");
    }
    private bool TryOpenHatchForCapture()
    {
        bool was = _suitOn; _suitOn = true;
        bool ok = !SiltStorm && (_sub.LinearVelocity - _sub.Water).Length() <= 0.6f;
        _suitOn = was;
        return ok;
    }
    private void LogGloam(string what)
    {
        GD.Print(_gloam == null ? $"[capture] gloam {what}: none" :
            $"[capture] gloam {what}: state {_gloam.Current} dist {GloamDistance:0} m, hull {_sub.Hull:0}%, floods {(_sub.LightsOn ? "ON" : "off")}, trip {_floodTrip:0} s, breaches {_hull.ActiveBreaches}");
    }
    private void LogProx()
    {
        if (_prox == null) return;
        var sb = new System.Text.StringBuilder("[capture] prox:");
        for (int i = 0; i < Solitude.Sub.ProximityPanel.Count; i++)
            sb.Append($" {Solitude.Sub.ProximityPanel.Labels[i]}=solid {_prox.Solid[i]:0.00}/contact {_prox.Contact[i]:0.00}");
        GD.Print(sb.ToString());
    }
    private float _captureHeading0;
    private void CaptureMarkStory() => CaptureSection("story");

    /// <summary>Find the nearest spot of a biome (spiral search, 40 m steps) and park the boat there.</summary>
    private void CaptureGoToBiome(Solitude.World.Biome biome)
    {
        Vector3 c = _spawnPoint;
        for (int ring = 1; ring < 120; ring++)
            for (int i = 0; i < ring * 8; i++)
            {
                float a = Mathf.Tau * i / (ring * 8), r = ring * 40f;
                float x = c.X + Mathf.Sin(a) * r, z = c.Z + Mathf.Cos(a) * r;
                if (_gen.BiomeAt(x, z) != biome) continue;
                if (biome == Solitude.World.Biome.Crystal && _gen.CrystalField(x, z) < 0.85f) continue;
                Vector3 p = new(x, _gen.Height(x, z) + 14f, z);
                _terrain.GenerateImmediate(p, 2);
                _sub.GlobalTransform = new Transform3D(Basis.Identity, p);
                _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
                _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
                _sub.LightsOn = true;
                EnterWalking(); EnterHelm(); _view = HelmView.Chase; ApplyHelmView();
                GD.Print($"[capture] {biome} found {r:0} m from spawn at depth {-_gen.Height(x, z):0}");
                return;
            }
        GD.Print($"[capture] {biome}: none within 4.8 km");
    }

    /// <summary>`-- --capture-from=creatures` skips straight to that section (after a short settle).</summary>
    private void ApplyCaptureFrom()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--capture-from=")) continue;
            string section = arg.Substring(15);
            int idx = _captureScript.FindIndex(s => s.act.Method.Name == section switch { "creatures" => nameof(CaptureMarkCreatures), "biomes" => nameof(CaptureMarkBiomes), "story" => nameof(CaptureMarkStory), "test" => nameof(CaptureMarkTest), "harpoon" => nameof(CaptureMarkHarpoon), "modules" => nameof(CaptureMarkModules), "listener" => nameof(CaptureMarkListener), "hydrophone" => nameof(CaptureMarkHydrophone), "whale" => nameof(CaptureMarkWhale), "eye" => nameof(CaptureMarkEye), "prox" => nameof(CaptureMarkProx), "gloam" => nameof(CaptureMarkGloam), "terminal" => nameof(CaptureMarkTerminal), "silt" => nameof(CaptureMarkSilt), "lab" => nameof(CaptureMarkLab), "wrecks" => nameof(CaptureMarkWrecks), "wear" => nameof(CaptureMarkWear), "hanna" => nameof(CaptureMarkHanna), "swim" => nameof(CaptureMarkSwim), "hold" => nameof(CaptureMarkHold), "crew" => nameof(CaptureMarkCrew), _ => "" });
            if (idx < 0) { GD.PushWarning($"[capture] unknown section {section}"); return; }
            float shift = _captureScript[idx].at - 7f;
            for (int i = 0; i < _captureScript.Count; i++) _captureScript[i] = (_captureScript[i].at - shift, _captureScript[i].act);
            _captureStep = idx;
        }
    }

    private void UpdateCapture(float dt)
    {
        if (_captureScript == null) return;
        _captureT += dt;
        if (_swimLog && (_swimLogT -= dt) <= 0f)
        {
            _swimLogT = 1f;
            GD.Print($"[swim] t={_captureT:0.0} speed={(_diver.Velocity - WaterAt(_diver.GlobalPosition)).Length():0.00} motion={_audio.SwimMotion:0.00} bubbles={_audio.BubbleCount} diving={_audio.Diving}");
        }
        if (_docCam != null)
        {
            // side (1), head-on (-1) or top-down (2) view of the ravager, lit by the camera's lamp.
            Transform3D rt = (_docTarget ?? _ravager).GlobalTransform;
            Vector3 f = -rt.Basis.Z, r = rt.Basis.X;
            float k = _docScale; // 1 = sized for the 15 m ravager
            Vector3 offset = _docSide switch { -1f => f * 22f * k + Vector3.Up * 3f * k, 2f => Vector3.Up * 26f * k + f * 2f * k, _ => r * 24f * k + Vector3.Up * 4f * k };
            Vector3 focus = rt.Origin + f * _docForward;   // e.g. frame the head instead of the body centre
            _docCam.GlobalPosition = focus + offset;
            _docCam.LookAt(focus, _docSide == 2f ? f : Vector3.Up);
        }
        while (_captureStep < _captureScript.Count && _captureT >= _captureScript[_captureStep].at)
            _captureScript[_captureStep++].act();
    }

    private void Shot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        string path = $"{_captureDir}/{name}.png";
        img.SavePng(path);
        GD.Print($"[capture] {name}: fps={Engine.GetFramesPerSecond()} depth={_sub.Depth:0.0} speed={_sub.ForwardSpeed:0.00} " +
                 $"heading={_sub.Heading:0} pitch={_sub.PitchDeg:0.0} roll={_sub.RollDeg:0.0} engine={_sub.EngineOutput:0.00} " +
                 $"hull={_sub.Hull:0} chunks={_terrain.LoadedCount} player={_player.Position} audioSkips={_audio.Skips} gameT={_captureT:0.0} realT={Time.GetTicksMsec() / 1000.0:0.0}");
    }
}
