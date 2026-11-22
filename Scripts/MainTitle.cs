using System;
using Godot;
using Solitude.Creatures;
using Solitude.Stations;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// The title screen (Tom's design): "S O L I T U D E" top-left over a live view of the boat moored
/// at a station, lane lights chasing, beacon strobing, snow drifting; now and then a Crystal Ray
/// glides through. Everything on screen is the real world — no backdrop.
/// The ravager, the Fangwyrm and ship survival systems sleep until you start the dive.
/// </summary>
public partial class Main
{
    private Camera3D _titleCam;
    private StationSite _titleSite;
    private Vector3 _titleCentre;
    private float _titleT, _titleBaseAngle, _titleRayTimer;
    private Swimmer _titleRay;
    private float _titleRayAge;
    private string _titleCaptureDir;
    private int _titleShot;

    private bool SkipTitle()
    {
        if (_captureDir != null) return true;
        foreach (var arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith("--start=") || arg == "--save-test") return true;
        return false;
    }

    private void EnterTitle()
    {
        _mode = Mode.Title;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--capture-title=")) { _titleCaptureDir = arg.Substring(16); DirAccess.MakeDirRecursiveAbsolute(_titleCaptureDir); }

        // Moor the boat at the starter station and quiet the world.
        _titleSite = _stations.Directory.Starter;
        _stations.ForceDock(_titleSite);
        _sub.LightsOn = true;
        _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
        _terrain.GenerateImmediate(_titleSite.Position, 2);
        Sleep(_ravager, true);
        Sleep(_creatures, true);
        Sleep(_systems, true);
        _hull.ProcessMode = ProcessModeEnum.Disabled; // nothing breaks on the title screen
        _player.InputEnabled = false;
        _rig.MonitorsActive = false;
        _rig.BridgeActive = false;
        _audio.Inside = false;

        // Frame the berth side of the station with the boat in front of it.
        Vector3 dock = _titleSite.DockPose.Origin;
        _titleCentre = (dock + _titleSite.Position + Vector3.Up * 10f) * 0.5f;
        Vector3 side = new Basis(Vector3.Up, _titleSite.Yaw) * Vector3.Right;   // the berth is on the station's +X
        Vector3 aft = new Basis(Vector3.Up, _titleSite.Yaw) * Vector3.Back;     // boats come in from +Z
        Vector3 view = (side * 0.8f + aft * 0.6f).Normalized();
        _titleBaseAngle = Mathf.Atan2(view.X, view.Z);
        _titleCam = new Camera3D { Name = "TitleCam", Fov = 58f, Near = 0.3f, Far = 240f };
        _game.AddChild(_titleCam);
        UpdateTitleCamera(0f);
        _titleCam.MakeCurrent();

        _hud.SetGameplayHud(false);
        _hud.ShowTitle(Seed, NewDive, () => GetTree().Quit(), HasSave() ? ContinueDive : null);
        _titleRayTimer = 22f + (float)GD.RandRange(0, 8);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        // Once per launch, the studio splash first (Tom's logo, under the sea); the title fades in after it.
        // Dev launches (model shots, tests: any command-line option but the splash test itself) skip it.
        bool devLaunch = System.Linq.Enumerable.Any(OS.GetCmdlineUserArgs(), x => x.StartsWith("--") && !x.StartsWith("--splash-shot"));
        if (!_splashShown && _titleCaptureDir == null && !devLaunch) ShowStudioSplash(BeginTitle);
        else BeginTitle();
    }

    private void BeginTitle()
    {
        _hud.FadeTo(0f, 3f);
        StartTitleMusic();
    }

    // ------------------------------------------------------------------ the studio splash

    private static bool _splashShown;

    private void ShowStudioSplash(Action then)
    {
        _splashShown = true;
        var splash = new Solitude.UI.StudioSplash { Name = "StudioSplash" };
        splash.Pinged += () => _audio.PlayPing();
        splash.Finished += () => then?.Invoke();
        AddChild(splash);

        // Dev: `-- --splash-shot=<dir>` saves a few frames of it, then quits.
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--splash-shot=")) continue;
            string dir = arg.Substring(14);
            DirAccess.MakeDirRecursiveAbsolute(dir);
            foreach (float at in new[] { 0.3f, 0.9f, 1.9f, 2.6f, 4.2f, 6.0f, 9.5f })
                GetTree().CreateTimer(at).Timeout += () =>
                {
                    GetViewport().GetTexture().GetImage().SavePng($"{dir}/splash_{at:0.00}.png".Replace(',', '.'));
                    GD.Print($"[splash] shot at {at:0.0} s");
                };
            GetTree().CreateTimer(9.8).Timeout += () => GetTree().Quit();
        }
    }

    // ------------------------------------------------------------------ Tom's title music

    private AudioStreamPlayer _titleMusic;
    private const float TitleMusicDb = -5f;

    private void StartTitleMusic()
    {
        var stream = GD.Load<AudioStream>("res://assets/audio/music/title_screen_music.wav");
        if (stream == null) return;
        _titleMusic = new AudioStreamPlayer { Name = "TitleMusic", Stream = stream, VolumeDb = -40f, Bus = "Master" };
        AddChild(_titleMusic);
        _titleMusic.Finished += () => _titleMusic?.Play(); // loop for as long as you linger
        _titleMusic.Play();
        if (_titleCaptureDir != null) GD.Print($"[title] music: {stream.GetLength():0.0} s, playing={_titleMusic.Playing}");
        CreateTween().TweenProperty(_titleMusic, "volume_db", Settings.MusicDb(TitleMusicDb), 4.0).SetTrans(Tween.TransitionType.Sine);
        _audio.Duck(-9f, 2f); // let the music lead; the hull still creaks underneath
    }

    private void StopTitleMusic(float seconds)
    {
        if (_titleMusic == null) return;
        var m = _titleMusic;
        _titleMusic = null;
        var tw = CreateTween();
        tw.TweenProperty(m, "volume_db", -50f, seconds).SetTrans(Tween.TransitionType.Sine);
        tw.TweenCallback(Callable.From(m.QueueFree));
        _audio.Duck(0f, seconds);
    }

    private static void Sleep(Node n, bool asleep)
    {
        n.ProcessMode = asleep ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        if (n is Node3D n3) n3.Visible = !asleep;
    }

    private void UpdateTitle(float dt)
    {
        _titleT += dt;
        UpdateTitleCamera(dt);

        // Now and then, not too often: a Crystal Ray glides through the shot.
        _titleRayTimer -= dt;
        if (_titleRay == null && _titleRayTimer <= 0f) SpawnTitleRay();
        if (_titleRay != null)
        {
            _titleRayAge += dt;
            if (_titleRay.Leaving || _titleRayAge > 70f)
            {
                _titleRay.QueueFree();
                _titleRay = null;
                _titleRayTimer = 55f + (float)GD.RandRange(0, 60);
            }
        }

    }

    private void UpdateTitleCamera(float dt)
    {
        // A slow sway around the berth (never behind the station), a gentle rise and fall.
        float a = _titleBaseAngle + 0.55f * Mathf.Sin(_titleT * 0.028f);
        float r = 40f + 5f * Mathf.Sin(_titleT * 0.019f);
        Vector3 pos = _titleCentre + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * r + Vector3.Up * (5f + 3f * Mathf.Sin(_titleT * 0.05f));
        float ground = _gen.Height(pos.X, pos.Z) + 4f;
        if (pos.Y < ground) pos.Y = ground;
        _titleCam.GlobalPosition = pos;
        Vector3 look = _titleCentre + Vector3.Up * (1.5f * Mathf.Sin(_titleT * 0.04f));
        _titleCam.LookAt(look, Vector3.Up);
        _titleCam.RotateObjectLocal(Vector3.Forward, 0.015f * Mathf.Sin(_titleT * 0.07f)); // the faintest drift
    }

    private void SpawnTitleRay()
    {
        // Cross the frame between the camera and the boat, left to right or right to left.
        Vector3 cam = _titleCam.GlobalPosition;
        Vector3 toScene = (_titleCentre - cam).Normalized();
        Vector3 right = toScene.Cross(Vector3.Up).Normalized();
        float dir = GD.Randf() < 0.5f ? -1f : 1f;
        Vector3 mid = cam + toScene * 26f + Vector3.Up * (float)GD.RandRange(-2.0, 4.0);
        // Start just outside the frame edge (half-width ≈ 27 m at 26 m with this FOV) so it enters soon.
        Vector3 from = mid - right * dir * 30f, to = mid + right * dir * 36f;
        _titleRay = new Swimmer
        {
            Profile = CreatureProfile.CrystalRay(), Gen = _gen, Audio = _audio,
            Focus = () => _titleCam.GlobalPosition, FocusIsDiver = () => false, Anchor = mid, Name = "TitleRay",
        };
        _game.AddChild(_titleRay);
        _titleRay.GlobalPosition = from;
        _titleRay.Transit(to);
        _titleRayAge = 0f;
    }

    /// <summary>Menu → the real game, starting exactly as it always has.</summary>
    private void NewDive()
    {
        if (_mode != Mode.Title) return;
        _mode = Mode.Walking; // stop menu input immediately
        StopTitleMusic(2.5f);
        _hud.HideTitle();
        _hud.FadeTo(1f, 1.2f);
        _audio.PlayAt("airlock", 0f, 0.5f);
        var tw = CreateTween();
        tw.TweenInterval(1.4);
        tw.TweenCallback(Callable.From(() =>
        {
            _titleRay?.QueueFree();
            _titleRay = null;
            _titleCam.QueueFree();
            _stations.ForceRelease();
            _sub.GlobalTransform = new Transform3D(Basis.Identity, _spawnPoint);
            _terrain.GenerateImmediate(_spawnPoint, 2);
            Sleep(_ravager, false);
            Sleep(_creatures, false);
            Sleep(_systems, false);
            _hull.ProcessMode = ProcessModeEnum.Inherit;
            _player.Transform = _interior.PlayerSpawn;
            _player.SetLook(0f, -0.05f);
            _hud.SetGameplayHud(true);
            EnterWalking();
            BeginDive();
        }));
    }

    /// <summary>The start of a dive: the intro card over the boat's interior.</summary>
    private void BeginDive()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.Intro("S O L I T U D E", $"depth {_sub.Depth:0} m   ·   seed {Seed}   ·   you are alone down here");
        _hud.SetHelp("WASD move · SHIFT run · SPACE jump · C/ALT crouch · E use · I hold · ESC pause · F11 fullscreen");
    }

    /// <summary>Dev: `-- --capture-title=dir` shoots the title screen, a forced ray flyby and the start.</summary>
    private float _titleCapT;

    private void UpdateTitleCapture(float dt)
    {
        if (_titleCaptureDir == null) return;
        _titleCapT += dt;
        float[] at = { 4f, 12f, 13f, 19f, 23f, 26f, 32f };
        if (_titleShot >= at.Length || _titleCapT < at[_titleShot]) return;
        switch (_titleShot)
        {
            case 0: case 1: TitleShot($"t{_titleShot}_title"); break;
            case 2: _titleRayTimer = 0f; break;
            case 3: case 4: TitleShot($"t{_titleShot}_ray"); GD.Print($"[title] ray at {_titleRay?.GlobalPosition.DistanceTo(_titleCam.GlobalPosition):0} m from camera"); break;
            case 5: NewDive(); break;
            case 6: TitleShot("t6_dive_started"); GD.Print($"[title] mode={_mode} subAt={_sub.GlobalPosition} docked={_stations.DockedAt != null}"); GetTree().Quit(); break;
        }
        _titleShot++;
    }

    private void TitleShot(string name) => GetViewport().GetTexture().GetImage().SavePng($"{_titleCaptureDir}/{name}.png");
}
