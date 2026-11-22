using System;
using Godot;
using Solitude.Audio;
using Solitude.Core;
using Solitude.Creatures;
using Solitude.Interior;
using Solitude.Player;
using Solitude.Sonar;
using Solitude.Sub;
using Solitude.Systems;
using Solitude.UI;
using Solitude.World;

namespace Solitude;

/// <summary>
/// Game director. Builds the scene in code and owns the player's mode:
///   Walking  – first person in the interior
///   Helm     – steering; views: Bridge (via forward screen) / Bow camera / External chase
///   Monitors – sensor bay camera wall; 1-6 select the big screen, F fullscreen
///   Sonar    – PPI scope console; ping, range, fullscreen
///   Holo     – orbit the holo table's terrain projection
///
/// Everything 3D renders into a low-resolution SubViewport that is upscaled with nearest
/// filtering and run through the PS1 post shader; the HUD sits on top at full resolution.
/// </summary>
public partial class Main : Node
{
	private enum Mode { Walking, Helm, Monitors, Sonar, Holo, Arm, Panel, Reading, Diving, Title }
	private enum HelmView { Bridge, BowCamera, Chase }

	[Export] public int PixelScale = 3;
	[Export] public int Seed = 0;

	private SubViewportContainer _container;
	private SubViewport _game;
	private ShaderMaterial _post;

	private TerrainGenerator _gen;
	private TerrainStreamer _terrain;
	private Ocean _ocean;
	private Submarine _sub;
	private HullCameraRig _rig;
	private SubInterior _interior;
	private PlayerController _player;
	private Camera3D _stationCam, _bowCam, _chaseCam;
	private AudioDirector _audio;
	private Hud _hud;
	private SonarSystem _sonar;
	private Ravager _ravager;
	private ShipSystems _systems;
	private ManipulatorArm _arm;
	private string _panelKind;
	private int _panelVersion = -1;
	private bool _holdVisible;
	private Vector3 _spawnPoint;
	private SubViewport _sonarScope;
	private float _holoYaw = 0.6f, _holoPitch = 0.75f;

	private Mode _mode = Mode.Walking;
	private HelmView _view = HelmView.Bridge;
	private int _selectedFeed;
	private bool _feedFullscreen;
	private float _damageFlash;
	private float _proximityBeep;

	private static readonly Vector3 InteriorOrigin = new(0, 5000f, 0);

	public override void _Ready()
	{
		GameInput.Setup();
		Settings.Load();
		GetTree().Paused = false; // in case we arrived here from the pause menu
		if (PendingLoad != null) Seed = PendingLoad.Seed; // CONTINUE: same world as the save
		if (Seed == 0) Seed = ParseSeed() ?? (int)(GD.Randi() % 1_000_000);
		GD.Print($"[Solitude] seed {Seed}");

		// ---- low-res game viewport + post
		_post = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/ps1_post.gdshader") };
		_container = new SubViewportContainer
		{
			Stretch = true,
			StretchShrink = PixelScale,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Material = _post,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_container);
		_game = new SubViewport
		{
			Msaa3D = Viewport.Msaa.Disabled,
			PositionalShadowAtlasSize = 2048,
			HandleInputLocally = false,
		};
		_container.AddChild(_game);

		// ---- world
		Props.Init();
		_gen = new TerrainGenerator(Seed);
		Vector3 spawn = FindSpawn();
		_spawnPoint = spawn;

		_ocean = new Ocean { Name = "Ocean" };
		_game.AddChild(_ocean);

		_terrain = new TerrainStreamer { Name = "Terrain", Gen = _gen };
		_game.AddChild(_terrain);
		_terrain.GenerateImmediate(spawn, 2);

		_sub = new Submarine { Name = "Submarine" };
		_game.AddChild(_sub);
		_sub.GlobalPosition = spawn;
		_sub.ApplyTelegraph();

		_terrain.Focus = FocusPoint;
		_ocean.Focus = FocusPoint;
		_ocean.Gen = _gen;

		_rig = new HullCameraRig { Name = "HullCameras", Sub = _sub };
		_game.AddChild(_rig);

		_bowCam = new Camera3D { Name = "BowCam", Position = new Vector3(0, 0.9f, -16.9f), Fov = 78f, Near = 0.2f, Far = 230f };
		_sub.AddChild(_bowCam);
		_chaseCam = new Camera3D { Name = "ChaseCam", Fov = 70f, Near = 0.3f, Far = 240f };
		_game.AddChild(_chaseCam);
		// The external view is a tethered drone with its own lamp, otherwise the hull is invisible.
		_chaseCam.AddChild(new SpotLight3D { SpotRange = 80f, SpotAngle = 32f, LightEnergy = 3.5f, LightVolumetricFogEnergy = 0.5f, LightColor = new Color(0.85f, 0.92f, 1f) });
		_chaseCam.GlobalPosition = _sub.GlobalTransform * new Vector3(0, 9f, 36f);

		// ---- sonar: system + 2D PPI scope rendered into its own small viewport
		_sonar = new SonarSystem { Name = "Sonar", Gen = _gen, Sub = _sub };
		AddChild(_sonar);
		_sonarScope = new SubViewport
		{
			Name = "SonarScope",
			Size = new Vector2I(256, 256),
			Disable3D = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
		};
		AddChild(_sonarScope);
		_sonarScope.AddChild(new SonarDisplay { Sonar = _sonar, Sub = _sub, Size = new Vector2(256, 256) });

		// ---- interior
		_interior = new SubInterior
		{
			Name = "Interior",
			Sub = _sub,
			Rig = _rig,
			Sonar = _sonar,
			SonarTexture = _sonarScope.GetTexture(),
			OnHelm = EnterHelm,
			OnMonitors = EnterMonitors,
			OnSonar = EnterSonar,
			OnHolo = EnterHolo,
			OnArm = EnterArm,
			OnPanel = EnterPanel,
			OnHatch = OnHatch,
			OnSuit = OnSuit,
			OnMessage = msg => { _hud.Message(msg); _audio.PlayBlip(330f, 0.12f); },
		};
		_game.AddChild(_interior);
		_interior.GlobalPosition = InteriorOrigin;
		_interior.Build();

		_player = new PlayerController { Name = "Player", Sub = _sub };
		_interior.AddChild(_player);
		_player.Transform = _interior.PlayerSpawn;
		_player.SetLook(0f, -0.05f);
		_player.Camera.Environment = _ocean.Interior;
		_player.Camera.MakeCurrent();

		_stationCam = new Camera3D { Name = "StationCam", Fov = 70f, Near = 0.05f, Far = 60f, Environment = _ocean.Interior };
		_interior.AddChild(_stationCam);

		SelectFeed(0);

		// ---- audio + HUD
		_audio = new AudioDirector { Name = "Audio" };
		AddChild(_audio);
		_hud = new Hud { Name = "Hud" };
		AddChild(_hud);
		_sonar.Audio = _audio;

		// ---- creatures
		_ravager = new Ravager { Name = "Ravager", Sub = _sub, Gen = _gen, Audio = _audio };
		_game.AddChild(_ravager);
		_sonar.Bodies = () => new[] { _ravager.GlobalPosition };

		// ---- ship systems + manipulator arm
		_systems = new ShipSystems
		{
			Name = "Systems", Sub = _sub, Terrain = _terrain, Gen = _gen, Audio = _audio,
			Message = msg => _hud.Message(msg, 4f),
		};
		AddChild(_systems);
		_arm = new ManipulatorArm
		{
			Name = "Arm", Terrain = _terrain, Gen = _gen, Systems = _systems,
			OnClaw = s => { _audio.PlayImpact(s * 0.35f); _audio.PlayBlip(140f, 0.06f, 0.6f); },
		};
		_sub.AddChild(_arm);

		// ---- stations (milestone 4)
		SetupStations(spawn);
		SetupDiving();
		SetupCreatures();
		SetupHullEvents();
		SetupStory();
		ApplyModules();

		_sub.Impact += s =>
		{
			_audio.PlayImpact(s);
			_player.AddShake(s * 0.6f);
			_damageFlash = Mathf.Min(1f, _damageFlash + s * 0.35f);
		};
		_player.Footstep += () => { if (!_player.Crouching || GD.Randf() < 0.5f) _audio.PlayStep(); };
		_player.Jumped += () => _audio.PlayStep();
		_player.Landed += speed =>
		{
			_audio.PlayStep();
			_audio.PlayStep();
			if (speed > 5f) _audio.PlayImpact(0.15f);
			if (_player.InStation) _station?.Noise(_player.GlobalPosition, 6f + speed);
		};

		SetupCapture();
		SetupDebugStart();
		SetupSaveTest();
		SetupPause();
		// Normal launches open on the title screen; dev launches (--start, --capture) go straight in.
		if (PendingLoad != null)
		{
			var save = PendingLoad;
			PendingLoad = null;
			ApplySave(save);
			EnterWalking();
			BeginDive();
		}
		else if (SkipTitle()) BeginDive();
		else EnterTitle();
	}

	private static int? ParseSeed()
	{
		foreach (var arg in OS.GetCmdlineUserArgs())
			if (arg.StartsWith("--seed=") && int.TryParse(arg.AsSpan(7), out int s)) return s;
		return null;
	}

	/// <summary>Spawn 35–45 m above the seabed somewhere not insanely deep.</summary>
	private Vector3 FindSpawn()
	{
		Vector3 best = new(0, _gen.Height(0, 0) + 18f, 0);
		float bestScore = float.MaxValue;
		for (int i = 0; i < 64; i++)
		{
			float x = (i % 8) * 120f - 420f, z = (i / 8) * 120f - 420f;
			float h = _gen.Height(x, z);
			float score = Mathf.Abs(h + 480f) - _gen.Normal(x, z, 8f).Y * 50f;
			if (score < bestScore) { bestScore = score; best = new Vector3(x, h + 18f, z); }
		}
		return best;
	}

	// ================================================================ modes

	private void EnterWalking()
	{
		_mode = Mode.Walking;
		_player.InputEnabled = true;
		_player.Camera.MakeCurrent();
		_feedFullscreen = false;
		_hud.ShowFeed(null, null, 1f);
		_hud.ShowHelm(false);
		_hud.HidePanel();
		_hud.ShowArm(false);
		_arm.Stow();
		_hud.SetCrosshair(true);
		_hud.SetHelp("WASD move · SHIFT run · SPACE jump · C/ALT crouch · E use · I hold · ESC pause · F11 fullscreen");
		_rig.MonitorsActive = true;
		_rig.BridgeActive = true;
		_audio.Inside = true;
		Input.MouseMode = Input.MouseModeEnum.Captured;
		if (_sub.Mode == DriveMode.Hold) _sub.TargetThrottle = 0f;
		_sub.BallastInput = 0f;
	}

	private void EnterHelm()
	{
		_mode = Mode.Helm;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.ShowHelm(true);
		_hud.SetHelp("W/S throttle · A/D + mouse rudder · R/F + mouse planes · SPACE/C ballast · P ping · G intake · K dock · X centre · L floods · V view · E leave");
		_audio.PlayBlip(660f, 0.08f);
		_view = HelmView.Bridge;
		MoveStationCam(_interior.GlobalTransform * _interior.HelmView * new Transform3D(new Basis(Vector3.Right, -0.06f), Vector3.Zero));
		ApplyHelmView();
	}

	private void EnterMonitors()
	{
		_mode = Mode.Monitors;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.SetHelp("1-6 or click: put feed on main screen · F fullscreen · E / ESC leave");
		_audio.PlayBlip(990f, 0.05f);
		Input.MouseMode = Input.MouseModeEnum.Visible;
		MoveStationCam(_interior.GlobalTransform * _interior.MonitorView);
	}

	private void EnterSonar()
	{
		_mode = Mode.Sonar;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.SetHelp("SPACE / click / P ping · Q range · F fullscreen scope · E / ESC leave");
		_audio.PlayBlip(990f, 0.05f);
		Input.MouseMode = Input.MouseModeEnum.Visible;
		MoveStationCam(_interior.GlobalTransform * _interior.SonarView);
	}

	private void EnterHolo()
	{
		_mode = Mode.Holo;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.SetHelp("mouse orbit · wheel / Q zoom · SPACE / P ping · E / ESC leave");
		_audio.PlayBlip(1100f, 0.05f);
		Input.MouseMode = Input.MouseModeEnum.Captured;
		// Start the orbit from roughly where the player is standing.
		Vector3 c = _interior.GlobalTransform * _interior.HoloCentre;
		Vector3 d = _player.Camera.GlobalPosition - c;
		_holoYaw = Mathf.Atan2(d.X, d.Z);
		_holoPitch = 0.75f;
		MoveStationCam(HoloCamTransform());
	}

	private Transform3D HoloCamTransform()
	{
		// Orbit in interior-local space, clamped so the camera never leaves the sensor bay.
		Vector3 c = _interior.HoloCentre;
		const float dist = 1.35f;
		Vector3 pos = c + new Vector3(Mathf.Sin(_holoYaw) * Mathf.Cos(_holoPitch), Mathf.Sin(_holoPitch), Mathf.Cos(_holoYaw) * Mathf.Cos(_holoPitch)) * dist;
		pos = new Vector3(Mathf.Clamp(pos.X, -2.2f, 2.3f), Mathf.Min(pos.Y, 2.4f), Mathf.Clamp(pos.Z, -5.7f, -0.3f));
		var local = Transform3D.Identity.Translated(pos).LookingAt(c, Vector3.Up);
		return _interior.GlobalTransform * local;
	}

	private void EnterArm()
	{
		if (_systems.Emergency) { _hud.Message("No power for the arm hydraulics."); return; }
		_mode = Mode.Arm;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.ShowArm(true);
		_hud.SetHelp("mouse move claw · wheel / W-S height · click grab · keep speed under 2.5 m/s · E / ESC stow and leave");
		_arm.Deploy();
		_arm.Camera.MakeCurrent();
		_rig.MonitorsActive = false;
		_audio.Inside = false;
		_audio.PlayBlip(500f, 0.1f);
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void EnterPanel(string kind)
	{
		_mode = Mode.Panel;
		_panelKind = kind;
		_panelVersion = -1;
		_player.InputEnabled = false;
		_hud.SetCrosshair(false);
		_hud.SetPrompt("");
		_hud.SetHelp("1-9 or click to use · E / ESC leave");
		_audio.PlayBlip(880f, 0.05f);
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Transform3D view = kind switch
		{
			"galley" => _interior.GalleyView,
			"workbench" => _interior.WorkbenchView,
			_ => _interior.EngineeringView,
		};
		MoveStationCam(_interior.GlobalTransform * view);
		RefreshPanel();
	}

	private readonly System.Collections.Generic.List<Action> _panelActions = new();

	/// <summary>Rebuild the station panel from current hold/power state.</summary>
	private void RefreshPanel()
	{
		_panelVersion = _systems.Hold.Version;
		var opts = new System.Collections.Generic.List<(string, bool)>();
		_panelActions.Clear();
		string title, body;
		var hold = _systems.Hold;

		void AddRecipes(Station st)
		{
			foreach (var r in Recipes.All)
			{
				if (r.Station != st) continue;
				bool ok = hold.Has(r.Cost) && _systems.Power >= r.PowerCost;
				opts.Add(($"{r.Verb} {Items.Name(r.Output)}   <-  {Recipes.CostText(r)}   ({r.PowerCost:0.#} pwr)", ok));
				var rec = r;
				_panelActions.Add(() =>
				{
					if (_systems.Craft(rec)) { _audio.PlayImpact(0.25f); _audio.PlayBlip(st == Station.Galley ? 300f : 700f, 0.15f, 0.4f); }
				});
			}
		}

		switch (_panelKind)
		{
			case "workbench":
				title = "WORKBENCH  —  process & fabricate";
				AddRecipes(Station.Workbench);
				// Modules: only the blueprints you know. Unknown ones are counted so you know there's more.
				opts.Add(($"## MODULES  —  {_modules.Known.Count}/{Modules.All.Length} blueprints known  (4 pwr each)", false));
				_panelActions.Add(() => { });
				foreach (var m in Modules.All)
				{
					if (!_modules.Known.Contains(m.Id)) continue;
					int have = hold.Count(m.Item) + (_modules.Has(m.Id) ? 1 : 0);
					opts.Add(($"Fabricate {m.Name}{(have > 0 ? $" (have {have})" : "")}: {m.Effect}   <-  {Modules.CostText(m)}", hold.Has(m.Cost) && _systems.Power >= 4f));
					var def = m;
					_panelActions.Add(() => FabricateModule(def));
				}
				break;
			case "galley":
				title = "GALLEY  —  cook & eat";
				AddRecipes(Station.Galley);
				foreach (var (id, count) in hold.All())
				{
					var info = Items.Info[id];
					if (info.Food <= 0f) continue;
					opts.Add(($"Eat {info.Name}  (+{info.Food:0} food)   x{count}", true));
					var food = id;
					_panelActions.Add(() => { _systems.Eat(food); _audio.PlayBlip(220f, 0.2f, 0.3f); });
				}
				break;
			default:
				title = "ENGINEERING  —  power bus & damage control";
				opts.Add(($"Install power cell  (+40 power)   x{hold.Count(ItemId.PowerCell)}", hold.Count(ItemId.PowerCell) > 0));
				_panelActions.Add(_systems.InstallPowerCell);
				opts.Add(($"Weld hull patch  (+30 hull)   x{hold.Count(ItemId.PatchKit)}", hold.Count(ItemId.PatchKit) > 0 && _sub.Hull < 99.5f));
				_panelActions.Add(_systems.ApplyPatch);
				opts.Add(($"{(_systems.IntakeOn ? "Close" : "Open")} intake filter", !_systems.Emergency));
				_panelActions.Add(_systems.ToggleIntake);
				// The module rack (milestone 7): fitted modules can be pulled; fabricated ones fitted.
				opts.Add(($"## MODULE RACK  —  {_modules.Used}/{_modules.Bays} bays", false));
				_panelActions.Add(() => { });
				foreach (var id in _modules.Fitted)
				{
					var m = Modules.Def(id);
					opts.Add(($"Remove {m.Name}  [{m.Effect}]", true));
					_panelActions.Add(() => RemoveModule(m));
				}
				foreach (var m in Modules.All)
				{
					if (hold.Count(m.Item) == 0 || _modules.Has(m.Id)) continue;
					string why = _modules.WhyNot(m);
					string swap = _modules.Displaces(m) is ModuleId d ? $" (replaces {Modules.Def(d).Name})" : "";
					opts.Add(($"Fit {m.Name}{swap}: {m.Effect}{(why != null ? $"   [{why}]" : "")}", why == null));
					var def = m;
					_panelActions.Add(() => FitModule(def));
				}
				if (_modules.Fitted.Count == 0 && !System.Linq.Enumerable.Any(Modules.All, m => hold.Count(m.Item) > 0))
				{
					opts.Add(("(no modules yet: fabricate them at the workbench)", false));
					_panelActions.Add(() => { });
				}
				break;
		}

		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"POWER {_systems.Power:0}%{(_systems.Thermal ? "  (thermal charging)" : "")}    HULL {_sub.Hull:0}%    FOOD {_systems.Food:0}%");
		sb.AppendLine($"HOLD {hold.Weight}/{hold.Capacity}:");
		var items = new System.Collections.Generic.List<string>();
		foreach (var (id, count) in hold.All()) items.Add($"{Items.Name(id)} x{count}");
		if (_panelKind == "workbench" && _modules.Known.Count < Modules.All.Length)
			items.Add("\n(More module blueprints are out there: every station core keeps one.)");
		sb.Append(items.Count > 0 ? string.Join(",  ", items) : "(empty)");
		body = sb.ToString();
		_hud.ShowPanel(title, body, opts, PickPanel);
	}

	private void PickPanel(int i)
	{
		if (i < 0 || i >= _panelActions.Count) return;
		_panelActions[i]();
		RefreshPanel();
	}

	private void TryPing()
	{
		if (!_systems.UsePower(1.5f)) { _hud.Message("Sonar: insufficient power."); _audio.PlayBlip(180f, 0.08f, 0.5f); return; }
		if (_sonar.Ping()) return;
		_systems.UsePower(-1.5f); // refund: still recharging
		_audio.PlayBlip(180f, 0.08f, 0.5f); // still recharging
	}

	private void MoveStationCam(Transform3D target)
	{
		_stationCam.GlobalTransform = _player.Camera.GlobalTransform;
		_stationCam.MakeCurrent();
		var tw = CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tw.TweenProperty(_stationCam, "global_transform", target, 0.6);
		_stationCam.SetMeta("tweening", true);
		tw.Finished += () => _stationCam.SetMeta("tweening", false);
	}

	private void ApplyHelmView()
	{
		switch (_view)
		{
			case HelmView.Bridge:
				_stationCam.MakeCurrent();
				_rig.BridgeActive = true;
				_rig.MonitorsActive = false;
				_audio.Inside = true;
				break;
			case HelmView.BowCamera:
				_bowCam.MakeCurrent();
				_rig.BridgeActive = false;
				_rig.MonitorsActive = false;
				_audio.Inside = false;
				break;
			case HelmView.Chase:
				_chaseCam.MakeCurrent();
				_rig.BridgeActive = false;
				_rig.MonitorsActive = false;
				_audio.Inside = false;
				break;
		}
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void SelectFeed(int i)
	{
		_selectedFeed = Math.Clamp(i, 0, _rig.Feeds.Length - 1);
		for (int k = 0; k < _rig.Feeds.Length; k++)
		{
			_rig.Feeds[k].Focused = k == _selectedFeed;
			_interior.SmallScreens[k].SetShaderParameter("selected", k == _selectedFeed ? 1f : 0f);
		}
		var feed = _rig.Feeds[_selectedFeed];
		_interior.BigScreen.SetShaderParameter("feed", feed.Viewport.GetTexture());
		_interior.BigScreenLabel.Text = feed.Name;
		if (_feedFullscreen) _hud.ShowFeed(feed.Viewport.GetTexture(), feed.Name, feed.Signal);
	}

	// ================================================================ input

	public override void _Input(InputEvent e)
	{
		// Esc pauses wherever it isn't already "back out of this station".
		if (e is InputEventKey esc && esc.Pressed && !esc.Echo && esc.PhysicalKeycode == Key.Escape
			&& _mode is Mode.Walking or Mode.Helm or Mode.Diving && !_endingRunning && !_pause.Visible)
		{
			OpenPause();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			Vector2 rel = mm.Relative * Settings.MouseSensitivity;
			if (_mode == Mode.Walking) { _player.Look(rel); _weapons.AddSway(rel); }
			else if (_mode == Mode.Diving) _diver.Look(rel);
			else if (_mode == Mode.Helm)
			{
				_sub.Rudder = Mathf.Clamp(_sub.Rudder + rel.X * 0.0025f, -1f, 1f);
				_sub.Planes = Mathf.Clamp(_sub.Planes - rel.Y * 0.0025f, -1f, 1f);
			}
			else if (_mode == Mode.Holo)
			{
				_holoYaw -= rel.X * 0.006f;
				_holoPitch = Mathf.Clamp(_holoPitch + rel.Y * 0.005f, 0.3f, 1.35f);
			}
			else if (_mode == Mode.Arm)
			{
				// Mouse right = claw to starboard (+X), mouse up = claw forward (-Z).
				_arm.Steer(rel.X * 0.008f, rel.Y * 0.008f, 0f);
			}
		}
		else if (e is InputEventMouseButton aw && aw.Pressed && _mode == Mode.Arm)
		{
			if (aw.ButtonIndex == MouseButton.WheelUp) _arm.Steer(0, 0, 0.25f);
			else if (aw.ButtonIndex == MouseButton.WheelDown) _arm.Steer(0, 0, -0.25f);
			else if (aw.ButtonIndex == MouseButton.Left && !_arm.Grab()) { }
		}
		else if (e is InputEventKey wk && wk.Pressed && !wk.Echo && _mode == Mode.Walking && _player.InStation && wk.PhysicalKeycode >= Key.Key1 && wk.PhysicalKeycode <= Key.Key3)
		{
			SelectWeaponKey((int)(wk.PhysicalKeycode - Key.Key1));
		}
		else if (e is InputEventKey pk && pk.Pressed && !pk.Echo && _mode == Mode.Panel)
		{
			int n = (int)(pk.PhysicalKeycode - Key.Key1);
			if (n >= 0 && n < 9) PickPanel(_hud.PanelKeyToIndex(n));
		}
		else if (e is InputEventMouseButton wheel && wheel.Pressed && _mode == Mode.Holo &&
				 (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
		{
			_interior.Holo.CycleZoom(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
			_audio.PlayBlip(1300f, 0.02f, 0.3f);
		}
		else if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
		{
			if (_mode == Mode.Sonar && !_feedFullscreen) TryPing();
			else if (_mode == Mode.Monitors && !_feedFullscreen) ClickScreen(mb.Position);
			// Click-to-recapture only where the mouse steers; never in menu-like modes (buttons live there).
			else if (Input.MouseMode != Input.MouseModeEnum.Captured && _mode is not (Mode.Monitors or Mode.Sonar or Mode.Title or Mode.Panel or Mode.Reading))
				Input.MouseMode = Input.MouseModeEnum.Captured;
		}
		else if (e is InputEventKey k && k.Pressed && !k.Echo && _mode == Mode.Monitors)
		{
			int n = (int)(k.PhysicalKeycode - Key.Key1);
			if (n >= 0 && n < 6) { SelectFeed(n); _audio.PlayBlip(1200f, 0.03f, 0.4f); }
		}
	}

	private void ClickScreen(Vector2 windowPos)
	{
		// Window pixels → low-res viewport pixels.
		Vector2 vpPos = windowPos / PixelScale;
		Vector3 from = _stationCam.ProjectRayOrigin(vpPos);
		Vector3 to = from + _stationCam.ProjectRayNormal(vpPos) * 5f;
		var hit = _stationCam.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, Layers.Interior));
		if (hit.Count > 0 && hit["collider"].AsGodotObject() is ScreenHit sh)
		{
			SelectFeed(sh.Index);
			_audio.PlayBlip(1200f, 0.03f, 0.4f);
		}
	}

	// ================================================================ per-frame

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		if (Input.IsActionJustPressed(GameInput.ToggleFullscreenWindow))
		{
			Settings.Fullscreen = DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Fullscreen;
			Settings.Apply();
			Settings.Save();
		}

		if (Input.IsActionJustPressed(GameInput.SummonCreature))
		{
			_ravager.ForcePass(40f, teleport: true);
			_hud.Message("[debug] something is crossing your bow", 3f);
		}

		switch (_mode)
		{
			case Mode.Walking: UpdateWalking(); UpdateAboard(dt); break;
			case Mode.Helm: UpdateHelm(dt); break;
			case Mode.Monitors: UpdateMonitors(); break;
			case Mode.Sonar: UpdateSonar(); break;
			case Mode.Holo: UpdateHolo(); break;
			case Mode.Arm: UpdateArm(dt); break;
			case Mode.Panel: UpdatePanel(); break;
			case Mode.Reading: UpdateReading(); break;
			case Mode.Diving: UpdateDiving(dt); break;
			case Mode.Title: UpdateTitle(dt); break;
		}
		UpdateVitals();

		UpdateChaseCam(dt);
		UpdateFeedsOnScreens();
		UpdateAudioAndPost(dt);
		UpdateWarnings(dt);
		UpdateHatchBeacon(dt);
		if (_mode != Mode.Title) { UpdateCreatureDebugKeys(); UpdateHullDebugKeys(); }
		_hull.Unease = _audio.Unease;
		UpdateTitleCapture(dt);
		UpdateBiomeNotice(dt);
		UpdateStory(dt);
		UpdateCapture(dt);
	}

	private void UpdateWalking()
	{
		_hud.SetPrompt(_player.Focused != null ? $"[E] {_player.Focused.Prompt}" : "");
		if (Input.IsActionJustPressed(GameInput.Interact) && _player.Focused != null)
			_player.Focused.Use();
	}

	private void UpdateHelm(float dt)
	{
		// E (or Tab) leaves the helm; Esc now opens the pause menu.
		if (Input.IsActionJustPressed(GameInput.Interact) || (Input.IsActionJustPressed(GameInput.Cancel) && !Input.IsKeyPressed(Key.Escape)))
		{
			EnterWalking();
			_audio.PlayBlip(440f, 0.08f);
			return;
		}

		if (Input.IsActionJustPressed(GameInput.DriveMode))
		{
			_sub.Mode = _sub.Mode == DriveMode.Hold ? DriveMode.Telegraph : DriveMode.Hold;
			if (_sub.Mode == DriveMode.Telegraph)
			{
				// Snap the telegraph to whatever is closest to the current command.
				int best = 2;
				for (int i = 0; i < Submarine.Telegraph.Length; i++)
					if (Mathf.Abs(Submarine.Telegraph[i].throttle - _sub.TargetThrottle) < Mathf.Abs(Submarine.Telegraph[best].throttle - _sub.TargetThrottle)) best = i;
				_sub.TelegraphIndex = best;
				_sub.ApplyTelegraph();
			}
			_hud.Message(_sub.Mode == DriveMode.Hold ? "DRIVE: HOLD-TO-RUN — engine runs while W/S held, controls self-centre" : "DRIVE: TELEGRAPH — W/S ring orders, controls stay where set", 3f);
			_audio.PlayBlip(520f, 0.1f);
		}

		float rudderKeys = Input.GetAxis(GameInput.Left, GameInput.Right);
		float planeKeys = Input.GetAxis(GameInput.PlanesDown, GameInput.PlanesUp);

		if (_sub.Mode == DriveMode.Telegraph)
		{
			if (Input.IsActionJustPressed(GameInput.Forward)) { _sub.TelegraphIndex++; _audio.PlayBlip(780f, 0.07f); _audio.PlayBlip(1170f, 0.05f, 0.3f); }
			if (Input.IsActionJustPressed(GameInput.Back)) { _sub.TelegraphIndex--; _audio.PlayBlip(780f, 0.07f); _audio.PlayBlip(1170f, 0.05f, 0.3f); }
			_sub.ApplyTelegraph();
			_sub.Rudder = Mathf.Clamp(_sub.Rudder + rudderKeys * 0.9f * dt, -1f, 1f);
			_sub.Planes = Mathf.Clamp(_sub.Planes + planeKeys * 0.9f * dt, -1f, 1f);
		}
		else
		{
			_sub.TargetThrottle = Input.GetAxis(GameInput.Back, GameInput.Forward);
			if (rudderKeys != 0f) _sub.Rudder = Mathf.Clamp(_sub.Rudder + rudderKeys * 1.6f * dt, -1f, 1f);
			else _sub.Rudder = Mathf.MoveToward(_sub.Rudder, 0f, 0.6f * dt);
			if (planeKeys != 0f) _sub.Planes = Mathf.Clamp(_sub.Planes + planeKeys * 1.6f * dt, -1f, 1f);
			else _sub.Planes = Mathf.MoveToward(_sub.Planes, 0f, 0.6f * dt);
		}

		_sub.BallastInput = Input.IsActionPressed(GameInput.BallastBlow) ? 1f : Input.IsActionPressed(GameInput.BallastFlood) ? -1f : 0f;

		if (Input.IsActionJustPressed(GameInput.Ping)) TryPing();
		HelmDocking();
		if (Input.IsActionJustPressed(GameInput.IntakeToggle)) _systems.ToggleIntake();
		if (Input.IsActionJustPressed(GameInput.CenterControls)) { _sub.Rudder = 0f; _sub.Planes = 0f; _audio.PlayBlip(400f, 0.05f); }
		if (Input.IsActionJustPressed(GameInput.Lights)) { _sub.LightsOn = !_sub.LightsOn; _audio.PlayBlip(220f, 0.04f, 0.8f); }
		if (Input.IsActionJustPressed(GameInput.CameraCycle))
		{
			_view = (HelmView)(((int)_view + 1) % 3);
			ApplyHelmView();
			_audio.PlayBlip(1400f, 0.03f, 0.3f);
		}

		float alt = _sub.GlobalPosition.Y - _gen.Height(_sub.GlobalPosition.X, _sub.GlobalPosition.Z);
		string view = _view switch { HelmView.Bridge => "BRIDGE [V]", HelmView.BowCamera => "CAM 1 BOW [V]", _ => "EXTERNAL [V]" };
		view += DockingText() + StoryHelmText();
		view += _sonar.RechargeLeft > 0 ? $"\nSONAR    CHARGING {_sonar.RechargeLeft:0.0}s" : "\nSONAR    READY [P]";
		_hud.UpdateHelm(_sub, alt, view);
	}

	private void UpdateMonitors()
	{
		if (Input.IsActionJustPressed(GameInput.Fullscreen))
		{
			_feedFullscreen = !_feedFullscreen;
			var f = _rig.Feeds[_selectedFeed];
			_hud.ShowFeed(_feedFullscreen ? f.Viewport.GetTexture() : null, f.Name, f.Signal);
			_audio.PlayBlip(_feedFullscreen ? 1000f : 700f, 0.05f);
		}
		if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact))
		{
			if (_feedFullscreen && Input.IsActionJustPressed(GameInput.Cancel))
			{
				_feedFullscreen = false;
				_hud.ShowFeed(null, null, 1f);
				return;
			}
			EnterWalking();
		}
	}

	private void UpdateArm(float dt)
	{
		float h = Input.GetAxis(GameInput.Back, GameInput.Forward);
		if (h != 0f) _arm.Steer(0, 0, h * 1.5f * dt);
		if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact) || !_arm.Deployed)
		{
			_audio.PlayBlip(400f, 0.1f);
			EnterWalking();
			return;
		}
		Vector3 tip = _arm.TipWorld();
		float alt = tip.Y - _gen.Height(tip.X, tip.Z);
		string near = _arm.Nearest != null
			? $"CONTACT  {Items.Name(_arm.Nearest.Type).ToUpperInvariant()}  {_arm.NearestDistance:0.0} m{(_arm.NearestDistance < 1.4f ? "   << IN REACH — CLICK" : "")}"
			: "CONTACT  none within 8 m";
		string state = _arm.State == ManipulatorArm.ArmState.Returning ? (_arm.Held != null ? "STOWING CATCH..." : "RETURNING...") : "ACTIVE";
		_hud.SetArmInfo($"ARM CAM  ·  {state}\nCLAW ALT {alt:0.0} m    SUB SPEED {_sub.ForwardSpeed:0.0} m/s\n{near}\nHOLD {_systems.Hold.Weight}/{_systems.Hold.Capacity}");
	}

	private void UpdatePanel()
	{
		if (_systems.Hold.Version != _panelVersion) RefreshPanel();
		if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact))
			EnterWalking();
	}

	private void UpdateVitals()
	{
		string pwr = _systems.Emergency ? "PWR EMERGENCY" : $"PWR {_systems.Power:0}%";
		_hud.SetVitals($"{pwr}{(_systems.Thermal ? " +THERMAL" : "")}    HULL {_sub.Hull:0}%    FOOD {_systems.Food:0}%    HOLD {_systems.Hold.Weight}/{_systems.Hold.Capacity}{(_systems.IntakeOn ? "    INTAKE" : "")}{StationVitals()}");
		if (_mode == Mode.Walking && Input.IsActionJustPressed(GameInput.Inventory)) _holdVisible = !_holdVisible;
		if (_holdVisible && _mode == Mode.Walking)
		{
			var lines = new System.Collections.Generic.List<string> { "HOLD  [I]" };
			foreach (var (id, count) in _systems.Hold.All()) lines.Add($"  {Items.Name(id)}  x{count}");
			_hud.SetHold(string.Join("\n", lines), true);
		}
		else _hud.SetHold("", false);
		_audio.Servo = _arm.ServoActivity;
		_audio.Intake = _systems.IntakeOn ? 1f : 0f;
	}

	private void UpdateSonar()
	{
		if (Input.IsActionJustPressed(GameInput.Jump) || Input.IsActionJustPressed(GameInput.Ping)) TryPing();
		if (Input.IsActionJustPressed(GameInput.SonarRange))
		{
			_sonar.CycleRange();
			_audio.PlayBlip(1300f, 0.03f, 0.4f);
		}
		if (Input.IsActionJustPressed(GameInput.Fullscreen))
		{
			_feedFullscreen = !_feedFullscreen;
			_hud.ShowFeed(_feedFullscreen ? _sonarScope.GetTexture() : null, "SONAR  -  ACTIVE", 1f, "[SPACE] ping   [Q] range   [F] close");
			_audio.PlayBlip(_feedFullscreen ? 1000f : 700f, 0.05f);
		}
		if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact))
		{
			if (_feedFullscreen && Input.IsActionJustPressed(GameInput.Cancel))
			{
				_feedFullscreen = false;
				_hud.ShowFeed(null, null, 1f);
				return;
			}
			EnterWalking();
		}
	}

	private void UpdateHolo()
	{
		if (Input.IsActionJustPressed(GameInput.Jump) || Input.IsActionJustPressed(GameInput.Ping)) TryPing();
		if (Input.IsActionJustPressed(GameInput.SonarRange))
		{
			_interior.Holo.ZoomIndex = (_interior.Holo.ZoomIndex + 1) % HoloTable.Zooms.Length;
			_audio.PlayBlip(1300f, 0.02f, 0.3f);
		}
		// Follow the orbit once the entry tween is done (tween owns the camera for 0.6 s).
		if (_stationCam.GetMeta("tweening", false).AsBool() == false)
			_stationCam.GlobalTransform = _stationCam.GlobalTransform.InterpolateWith(HoloCamTransform(), 0.35f);
		if (Input.IsActionJustPressed(GameInput.Cancel) || Input.IsActionJustPressed(GameInput.Interact))
			EnterWalking();
	}

	private void UpdateChaseCam(float dt)
	{
		Transform3D st = _sub.GlobalTransform;
		Vector3 back = st.Basis.Z; back.Y = 0;
		back = back.LengthSquared() > 1e-4f ? back.Normalized() : Vector3.Back;
		Vector3 target = _sub.GlobalPosition + back * 34f + Vector3.Up * 9f;
		float ground = _gen.Height(target.X, target.Z) + 3f;
		if (target.Y < ground) target.Y = ground;
		_chaseCam.GlobalPosition = _chaseCam.GlobalPosition.Lerp(target, Mathf.Clamp(1.8f * dt, 0f, 1f));
		Vector3 look = _sub.GlobalPosition - st.Basis.Z * 6f;
		if (!_chaseCam.GlobalPosition.IsEqualApprox(look))
			_chaseCam.LookAt(look, Vector3.Up);
	}

	private void UpdateFeedsOnScreens()
	{
		for (int i = 0; i < _rig.Feeds.Length; i++)
			_interior.SmallScreens[i].SetShaderParameter("signal", _rig.Feeds[i].Signal);
		_interior.BigScreen.SetShaderParameter("signal", _rig.Feeds[_selectedFeed].Signal);
	}

	private void UpdateAudioAndPost(float dt)
	{
		_audio.Depth = _sub.Depth;
		_audio.Stress = _sub.Stress;
		_audio.EngineRpm = _sub.EngineOutput;
		_audio.Speed = _sub.ForwardSpeed;
		_audio.TurnRate = _sub.YawRate;
		_audio.Hull = _sub.Hull;

		_damageFlash = Mathf.MoveToward(_damageFlash, 0f, dt * 1.2f);
		float hullDamage = 1f - _sub.Hull / 100f;
		_post.SetShaderParameter("damage", Mathf.Max(_damageFlash, hullDamage * 0.3f));
		bool outside = _mode == Mode.Helm && _view != HelmView.Bridge;
		if (_mode != Mode.Diving) _post.SetShaderParameter("wobble", outside ? 1f : 0f);
	}

	private void UpdateWarnings(float dt)
	{
		string warn = "";
		if (_sub.Depth > _sub.CrushDepth) warn = "!! CRUSH DEPTH EXCEEDED — HULL FAILING !!";
		else if (_sub.Depth > _sub.TestDepth) warn = "HULL STRESS — BELOW TEST DEPTH";
		else if (_sub.Hull < 35f) warn = "HULL INTEGRITY CRITICAL";
		if (_hull.Warning() is string hw) warn = hw; // a breach outranks everything

		if (_mode == Mode.Helm)
		{
			float alt = _sub.GlobalPosition.Y - _gen.Height(_sub.GlobalPosition.X, _sub.GlobalPosition.Z);
			if (alt < 9f && warn == "")
			{
				warn = "TERRAIN — PULL UP";
				_proximityBeep -= dt;
				if (_proximityBeep <= 0f) { _audio.PlayBlip(1500f, 0.06f, 0.5f); _proximityBeep = 0.25f + alt * 0.05f; }
			}
		}
		_hud.SetWarning(warn);
	}
}
