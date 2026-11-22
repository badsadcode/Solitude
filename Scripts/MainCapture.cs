using System;
using System.Collections.Generic;
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

    private void SetupCapture()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--capture=")) _captureDir = arg.Substring(10);
        if (_captureDir == null) return;
        DirAccess.MakeDirRecursiveAbsolute(_captureDir);
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

    private void UpdateCapture(float dt)
    {
        if (_captureScript == null) return;
        _captureT += dt;
        if (_docCam != null)
        {
            // side (1), head-on (-1) or top-down (2) view of the ravager, lit by the camera's lamp.
            Transform3D rt = _ravager.GlobalTransform;
            Vector3 f = -rt.Basis.Z, r = rt.Basis.X;
            Vector3 offset = _docSide switch { -1f => f * 22f + Vector3.Up * 3f, 2f => Vector3.Up * 26f + f * 2f, _ => r * 24f + Vector3.Up * 4f };
            _docCam.GlobalPosition = rt.Origin + offset;
            _docCam.LookAt(rt.Origin, _docSide == 2f ? f : Vector3.Up);
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
