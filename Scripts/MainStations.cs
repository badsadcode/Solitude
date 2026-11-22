using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Player;
using Solitude.Sonar;
using Solitude.Stations;
using Solitude.Systems;
using Environment = Godot.Environment;

namespace Solitude;

/// <summary>
/// Milestone 4 glue: stations in the world, docking from the helm, boarding through the dive
/// hatch, life aboard (weapons, flashlight, medkits, creatures, logs, map fragments), death,
/// and getting back to the boat.
/// </summary>
public partial class Main
{
    private StationManager _stations;
    private StationInterior _station;
    private Weapons _weapons;
    private Environment _stationEnv;
    private StationLogs.Log _reading;
    private bool _firePrev;
    private float _noiseTimer;

    private static readonly Vector3 StationOrigin = new(0, 8000f, 0);
    private static readonly Vector3 SubHatchSpot = new(-0.3f, 0.05f, 12.1f); // engineering, by the ladder

    private void SetupStations(Vector3 spawn)
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--layout=")) StationDirectory.ForceLayout = arg.Substring(9);
            else if (arg == "--station-dark") StationInterior.ForceDark = true;
        _stations = new StationManager
        {
            Name = "Stations", Sub = _sub, Audio = _audio,
            Directory = new StationDirectory(_gen, spawn),
            Message = m => _hud.Message(m, 5f),
        };
        _game.AddChild(_stations);

        _weapons = new Weapons { Name = "Weapons", CarrierSpeed = () => new Vector2(_player.Velocity.X, _player.Velocity.Z).Length() };
        _player.Camera.AddChild(_weapons);
        _weapons.Shot += w => _audio.PlayAt(w switch { WeaponId.Rifle => "rifle", WeaponId.Shotgun => "shotgun", _ => "pistol" }, 0f, 1f);
        _weapons.Noise += (pos, radius) => _station?.Noise(pos, radius);
        _weapons.EmptyClick += () => _audio.PlayAt("empty", 0f, 1f);
        _weapons.Reloaded += _ => _audio.PlayAt("reload", 0.2f, 1f);

        _stationEnv = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.12f, 0.14f, 0.15f),
            AmbientLightEnergy = 0.18f,
            TonemapMode = Environment.ToneMapper.Filmic,
            SsaoEnabled = true, SsaoRadius = 1.0f, SsaoIntensity = 2f,
            GlowEnabled = true, GlowIntensity = 0.8f, GlowBloom = 0.05f,
            FogEnabled = true, FogMode = Environment.FogModeEnum.Exponential,
            FogLightColor = new Color(0.01f, 0.015f, 0.015f), FogDensity = 0.05f,
        };

        _player.Hurt += dmg =>
        {
            _damageFlash = Mathf.Min(1f, _damageFlash + 0.25f + dmg * 0.02f);
            _audio.PlayAt("hurt", 0f, 0.5f);
        };
        _player.Died += OnPlayerDied;

        // Sonar hears stations (big bright structures) as well as creatures. Structures are kept apart
        // from bodies so the proximity panel doesn't call a station a "moving contact".
        _sonar.Bodies = () => new List<Vector3> { _ravager.GlobalPosition };
        _sonar.Structures = () =>
        {
            var list = new List<Vector3>();
            foreach (var s in _stations.Directory.Near(_sub.GlobalPosition, 600f))
                for (int i = 0; i < 6; i++) list.Add(s.Position + new Basis(Vector3.Up, s.Yaw) * new Vector3((i % 3 - 1) * 6f, 10f, (i / 3 - 0.5f) * 18f));
            list.AddRange(WreckStructures(_sub.GlobalPosition, 800f));
            return list;
        };
        _interior.Holo.Beacons = () =>
        {
            var list = new List<Vector3>();
            foreach (var s in _stations.Known(_sub.GlobalPosition, 2500f)) list.Add(s.Position);
            return list;
        };
    }

    // ------------------------------------------------------------------ helm: docking

    private void HelmDocking()
    {
        if (!Input.IsActionJustPressed(GameInput.Dock)) return;
        if (_stations.DockedAt != null)
        {
            if (_station != null && _player.InStation) { _hud.Message("You can't undock with nobody at the helm."); return; }
            FreeStation();
            _stations.Undock();
        }
        else if (!_stations.TryDock(() => Autosave("docked")))
        {
            _hud.Message(_stations.Nearest != null && _stations.NearestDistance < StationManager.GuidanceRange
                ? "DOCKING: not in the capture zone. Line up with the lane, under 9 m from the berth, below 1.3 m/s."
                : "DOCKING: no berth in range.", 4f);
            _audio.PlayAt("deny", 0f, 0.8f);
        }
    }

    private string DockingText()
    {
        var sb = new System.Text.StringBuilder();
        if (_stations.DockedAt != null)
            sb.Append($"\nDOCKED   {_stations.DockedAt.Name}   [K] undock");
        else if (_stations.Docking)
            sb.Append("\nDOCKING  clamps engaged...");
        else if (_stations.Nearest != null && _stations.NearestDistance < StationManager.GuidanceRange + 60f)
        {
            sb.Append($"\nBERTH    {_stations.DockDistance,5:0.0} m   LAT {_stations.DockLateral:+0.0;-0.0}   VERT {_stations.DockVertical:+0.0;-0.0}   HDG {_stations.DockHeadingError:+0;-0}°");
            sb.Append(_stations.InCaptureZone ? "\n         >> CAPTURE ZONE — [K] ENGAGE CLAMPS <<" : $"\n         closing {_stations.DockClosing:0.0} m/s   enter the lane from astern");
        }
        if (_stations.NearestKnown != null && _stations.DockedAt == null)
        {
            Vector3 d = _stations.NearestKnown.Position - _sub.GlobalPosition;
            float brg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(d.X, -d.Z)), 360f);
            sb.Append($"\nBEACON   {_stations.NearestKnown.Name}   BRG {Mathf.RoundToInt(brg) % 360:000}°   {(_stations.NearestKnownDistance > 1000f ? $"{_stations.NearestKnownDistance / 1000f:0.0} km" : $"{_stations.NearestKnownDistance:0} m")}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ boarding

    /// <summary>The dive hatch in engineering: onto the station when docked, otherwise sealed.</summary>
    private void OnHatch()
    {
        if (_stations.DockedAt == null)
        {
            if (_stations.Docking) { _hud.Message("Hold on — clamps still engaging."); _audio.PlayAt("deny", 0f, 0.6f); return; }
            TryDiveFromHatch();   // not docked: the hatch opens onto the sea (milestone 5)
            return;
        }
        var site = _stations.DockedAt;
        if (_station == null || _station.Site != site)
        {
            FreeStation();
            _station = new StationInterior
            {
                Name = "StationInterior", Site = site, State = _stations.StateOf(site),
                Player = _player, Systems = _systems, Weapons = _weapons,
                Message = m => _hud.Message(m, 5f),
                ReadLog = ShowLog,
                MapFragment = OnMapFragment,
                Blueprint = OnBlueprint,
                ReturnToSub = ReturnToSub,
                Sound = PlaySpatial,
            };
            _game.AddChild(_station);
            _station.GlobalPosition = StationOrigin;
        }
        _player.InputEnabled = false;
        _audio.PlayAt("airlock", 0f, 1f);
        _hud.FadeTo(1f, 0.8f);
        // Give the station a few frames to place contents, then step through.
        var tw = CreateTween();
        tw.TweenInterval(1.6);
        tw.TweenCallback(Callable.From(EnterStation));
    }

    private void EnterStation()
    {
        if (_station == null || !_station.Ready)
        {
            var tw = CreateTween();
            tw.TweenInterval(0.2);
            tw.TweenCallback(Callable.From(EnterStation));
            return;
        }
        _player.GlobalTransform = _station.GlobalTransform * _station.AirlockPose;
        _player.Velocity = Vector3.Zero;
        _player.SetLook(0f, 0f);
        _player.InStation = true;
        _player.InputEnabled = true;
        _player.Camera.Environment = _stationEnv;
        _weapons.SetActive(true);
        _rig.MonitorsActive = false;
        _rig.BridgeActive = false;
        _audio.Inside = true;
        _hud.FadeTo(0f, 1.2f);
        _hud.SetHelp("WASD move · SHIFT run · SPACE jump · C/ALT crouch · E use · LMB fire · R reload · 1-3 weapons · F flashlight · H medkit · I hold");
        _hud.Message(_station.Powered ? $"{_station.Site.Name}. The lights are on. Nobody answers." : $"{_station.Site.Name}. No power. Only the emergency strips, and your torch (F).", 6f);
        HannaStationEntered();
    }

    private void ReturnToSub()
    {
        _player.InputEnabled = false;
        _audio.PlayAt("airlock", 0f, 1f);
        _hud.FadeTo(1f, 0.8f);
        var tw = CreateTween();
        tw.TweenInterval(1.2);
        tw.TweenCallback(Callable.From(() =>
        {
            _player.InStation = false;
            _weapons.SetActive(false);
            _player.Flashlight.Visible = false;
            _player.Camera.Environment = _ocean.Interior;
            _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), SubHatchSpot);
            _player.Velocity = Vector3.Zero;
            _player.InputEnabled = true;
            _rig.MonitorsActive = true;
            _rig.BridgeActive = true;
            _hud.FadeTo(0f, 1.0f);
            _hud.SetHelp("WASD move · SHIFT run · SPACE jump · C/ALT crouch · E use · I hold · ESC pause · F11 fullscreen");
            Autosave("back aboard");
        }));
    }

    private void FreeStation()
    {
        if (_station == null) return;
        _station.QueueFree();
        _station = null;
    }

    // ------------------------------------------------------------------ hiding

    /// <summary>The view through the slats, your breathing, and your heart when something comes close.</summary>
    private void UpdateHidingSenses()
    {
        bool hidden = _player.Hidden && _station != null;
        _hud.Hide.Visible = hidden;
        _audio.Hiding = hidden;
        if (!hidden)
        {
            // Just came out: hands and heart back to normal.
            if (_hidingHeart) { _audio.Heartbeat = 0f; _audio.BreathHeld = false; _weapons.Visible = true; _hidingHeart = false; }
            return;
        }
        _weapons.Visible = false;   // both hands on the door
        float near = _station.NearestCrawler();
        float fear = Mathf.Clamp((9f - near) / 7f, 0f, 1f);
        _hud.Hide.Desk = _station.HidingUnderDesk;
        _hud.Hide.Breath01 = _station.Breath01;
        _hud.Hide.Holding = _station.HoldingBreath;
        _hud.Hide.Fear = fear;
        _audio.BreathHeld = _station.HoldingBreath;
        _audio.Heartbeat = fear;
        _hidingHeart = true;
    }
    private bool _hidingHeart;

    // ------------------------------------------------------------------ aboard

    private void UpdateAboard(float dt)
    {
        UpdateHidingSenses();
        if (!_player.InStation || _player.Dead || !_player.InputEnabled || _player.Hidden) return;
        bool held = Input.IsMouseButtonPressed(MouseButton.Left) && Input.MouseMode == Input.MouseModeEnum.Captured;
        bool pressed = held && !_firePrev;
        _firePrev = held;
        var exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
        _weapons.Trigger(held, pressed, _player.Camera, _player.GetWorld3D().DirectSpaceState, exclude);
        if (Input.IsActionJustPressed(GameInput.Reload)) _weapons.Reload();
        if (Input.IsActionJustPressed(GameInput.Flashlight)) { _player.Flashlight.Visible = !_player.Flashlight.Visible; _audio.PlayAt("empty", 0.3f, 0.6f); }
        if (Input.IsActionJustPressed(GameInput.UseMedkit))
        {
            if (_player.Health >= 99f) _hud.Message("You're not hurt. Not yet.");
            else if (_systems.Hold.Remove(ItemId.Medkit)) { _player.Heal(45f); _hud.Message("Autoinjector. The pain goes somewhere else."); _audio.PlayAt("pickup", 0f, 0.6f); }
            else _hud.Message("No medkits in the hold.");
        }

        // Footsteps are noise too: running is loud.
        _noiseTimer -= dt;
        float speed = new Vector2(_player.Velocity.X, _player.Velocity.Z).Length();
        if (_noiseTimer <= 0f && speed > 0.5f)
        {
            _noiseTimer = 0.4f;
            _station?.Noise(_player.GlobalPosition, _player.Crouching ? 1.5f : speed > 3.5f ? 9f : 3f);
        }
    }

    private void SelectWeaponKey(int n)
    {
        if (!_player.InStation) return;
        var id = (WeaponId)n;
        if (!_weapons.Owned.Contains(id)) { _hud.Message("You don't have that weapon."); return; }
        _weapons.Select(id);
        _audio.PlayAt("reload", 0f, 0.5f);
    }

    /// <summary>Pan and attenuate a world-space sound for the listener (the player camera).</summary>
    private void PlaySpatial(string kind, Vector3 pos)
    {
        var cam = _diver != null && _diver.Active ? _diver.Camera : _player.Camera;
        Vector3 rel = cam.GlobalBasis.Inverse() * (pos - cam.GlobalPosition);
        float dist = rel.Length();
        float range = kind switch
        {
            "screech" or "die" or "door" or "breach" => 45f,
            "knock" => 60f,          // the whole hull carries it
            "chitter" or "steam" => 22f,
            "drip" => 9f,
            "weld" => 12f,
            "reel" or "harpoon" => 14f,
            "shock" or "spearhit" => 35f,
            _ => 30f,
        };
        float gain = Mathf.Pow(Mathf.Clamp(1f - dist / range, 0f, 1f), 1.4f);
        float pan = Mathf.Clamp(rel.X / Mathf.Max(1f, dist), -1f, 1f);
        _audio.PlayAt(kind, pan, gain);
    }

    private void OnPlayerDied()
    {
        _player.Unhide();   // never wake up stuck in a locker
        if (_diver.Active) _diver.SetPhysicsProcess(false);
        _player.InputEnabled = false;
        _weapons.SetActive(false);
        _hud.FadeTo(1f, 2.5f);
        _hud.Message("The station keeps you.", 6f);
        _audio.PlayAt("die", 0f, 0.4f);
        var tw = CreateTween();
        tw.TweenInterval(3.5);
        tw.TweenCallback(Callable.From(() =>
        {
            // You wake up in your bunk. You don't remember the walk back (or the swim).
            if (_diver.Active) { _diver.Bag.Clear(); EndDive(); }
            _player.Revive(55f);
            _player.InStation = false;
            _player.Flashlight.Visible = false;
            _player.Camera.Environment = _ocean.Interior;
            _player.Transform = new Transform3D(Basis.Identity, new Vector3(-0.6f, 0.05f, 7f));
            _player.Velocity = Vector3.Zero;
            _player.InputEnabled = true;
            _rig.MonitorsActive = true;
            _rig.BridgeActive = true;
            EnterWalking();
            _hud.FadeTo(0f, 3f);
            _hud.Message("You wake in your bunk, shaking. You don't remember the way back.", 6f);
        }));
    }

    // ------------------------------------------------------------------ logs & map fragments

    private void ShowLog(StationLogs.Log log)
    {
        _reading = log;
        // A voiced crew entry plays in their own voice while you read it (the text is on screen).
        if (log.VoiceId != null) { _voiceQueue.Clear(); _voice?.Stop(); _logVoice = QueueVoice(log.VoiceId, null, 0.2f); }
        if (_shipLog.AddNote(log.Title, log.Body, _station?.Site.Name ?? "unknown station"))
            Log($"Note found on {_station?.Site.Name ?? "a station"}: \"{log.Title}\". Copied to the journal.");
        _mode = Mode.Reading;
        _player.InputEnabled = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _hud.ShowPanel(log.Title, log.Body, new List<(string, bool)> { ("Put it down", true) }, _ => CloseLog());
        _audio.PlayAt("pickup", 0f, 0.5f);
    }

    private void UpdateReading()
    {
        if (_endingRunning) return; // the epilogue closes only through its own buttons
        if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact)) CloseLog();
    }

    private bool _logVoice;

    private void CloseLog()
    {
        if (_mode != Mode.Reading) return;
        if (_logVoice) { _logVoice = false; _voiceQueue.Clear(); _voice?.Stop(); }   // put it down, it stops
        _hud.HidePanel();
        _mode = Mode.Walking;
        _player.InputEnabled = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>The station's core memory: the seabed around it, and where its neighbours are.</summary>
    private void OnMapFragment(StationSite site)
    {
        const float radius = 420f;
        double now = SonarSystem.Now;
        var cells = new List<(Vector2I, float, double)>();
        int r = (int)(radius / SonarMap.Cell);
        var c0 = SonarMap.CellOf(site.Position.X, site.Position.Z);
        for (int z = -r; z <= r; z++)
            for (int x = -r; x <= r; x++)
            {
                if (x * x + z * z > r * r) continue;
                var c = new Vector2I(c0.X + x, c0.Y + z);
                var w = SonarMap.CellCentre(c);
                cells.Add((c, _gen.Height(w.X, w.Y), now + Mathf.Sqrt(x * x + z * z) * 0.01));
            }
        _sonar.Map.Reveal(cells);

        int found = 0;
        var near = new List<StationSite>(_stations.Directory.Near(site.Position, 3500f));
        near.Sort((a, b) => a.Position.DistanceTo(site.Position).CompareTo(b.Position.DistanceTo(site.Position)));
        foreach (var s in near)
        {
            if (s == site || s.Known) continue;
            s.Known = true;
            if (++found >= 2) break;
        }
        _hud.Message($"MAP FRAGMENT: seabed charted for {radius:0} m around {site.Name}." + (found > 0 ? $" {found} more station beacon{(found > 1 ? "s" : "")} plotted on the holo table." : ""), 7f);

        // The core also holds a fragment of the Sounding (milestone 6) — shown a moment later.
        var tw = CreateTween();
        tw.TweenInterval(4.0);
        tw.TweenCallback(Callable.From(() => OnDataCore(site)));
    }

    private string StationVitals()
    {
        if (!_player.InStation) return "";
        var d = Weapons.Defs[_weapons.Current];
        string reload = _weapons.Reloading ? "  RELOADING" : "";
        return $"    HP {_player.Health:0}    {d.Name} {_weapons.Mag[_weapons.Current]}/{_weapons.Reserve[_weapons.Current]}{reload}    MEDKITS {_systems.Hold.Count(ItemId.Medkit)}{(_station?.State.HasKeycard == true ? "    RED KEY" : "")}";
    }
}
