using System;
using System.Collections.Generic;
using Godot;

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
    private float _docSide = 1f;

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
