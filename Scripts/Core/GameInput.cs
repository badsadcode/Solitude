using Godot;

namespace Solitude.Core;

/// <summary>Registers all input actions in code so project.godot stays readable.</summary>
public static class GameInput
{
    public const string Forward = "move_forward";
    public const string Back = "move_back";
    public const string Left = "move_left";
    public const string Right = "move_right";
    public const string Sprint = "sprint";
    public const string Interact = "interact";
    public const string Cancel = "cancel";
    public const string Jump = "jump";
    public const string Crouch = "crouch";

    // Helm
    public const string BallastBlow = "ballast_blow";
    public const string BallastFlood = "ballast_flood";
    public const string PlanesUp = "planes_up";
    public const string PlanesDown = "planes_down";
    public const string DriveMode = "drive_mode";
    public const string CenterControls = "center_controls";
    public const string CameraCycle = "camera_cycle";
    public const string Lights = "lights";

    // Sonar
    public const string Ping = "sonar_ping";
    public const string SonarRange = "sonar_range";

    // Systems
    public const string Inventory = "inventory";
    public const string IntakeToggle = "intake_toggle";

    // Stations
    public const string Dock = "dock";
    public const string Reload = "reload";
    public const string Flashlight = "flashlight";
    public const string UseMedkit = "use_medkit";
    public const string HarpoonMode = "harpoon_mode";

    // Debug
    public const string SummonCreature = "debug_summon_creature";
    public const string SummonRays = "debug_summon_rays";
    public const string DebugBreach = "debug_breach";
    public const string DebugKnock = "debug_knock";
    public const string DebugListener = "debug_listener";
    public const string SummonWyrm = "debug_summon_wyrm";
    public const string SummonWhale = "debug_summon_whale";
    public const string DebugEye = "debug_eye";

    // Monitor station
    public const string Fullscreen = "feed_fullscreen";
    public const string ToggleFullscreenWindow = "window_fullscreen";

    public static void Setup()
    {
        Bind(Forward, Godot.Key.W, Godot.Key.Up);
        Bind(Back, Godot.Key.S, Godot.Key.Down);
        Bind(Left, Godot.Key.A, Godot.Key.Left);
        Bind(Right, Godot.Key.D, Godot.Key.Right);
        Bind(Sprint, Godot.Key.Shift);
        Bind(Interact, Godot.Key.E);
        Bind(Cancel, Godot.Key.Escape, Godot.Key.Tab);
        Bind(Jump, Godot.Key.Space);
        Bind(Crouch, Godot.Key.C, Godot.Key.Alt); // never Ctrl (Tom: "control for crouching is a heresy")

        Bind(BallastBlow, Godot.Key.Space);
        Bind(BallastFlood, Godot.Key.Ctrl, Godot.Key.C);
        Bind(PlanesUp, Godot.Key.R);
        Bind(PlanesDown, Godot.Key.F);
        Bind(DriveMode, Godot.Key.T);
        Bind(CenterControls, Godot.Key.X);
        Bind(CameraCycle, Godot.Key.V);
        Bind(Lights, Godot.Key.L);
        Bind(Ping, Godot.Key.P);
        Bind(SonarRange, Godot.Key.Q);
        Bind(HarpoonMode, Godot.Key.Q);   // diving only: spear <-> shock prod
        Bind(SummonCreature, Godot.Key.F9);
        Bind(SummonRays, Godot.Key.F8);
        Bind(DebugBreach, Godot.Key.F7);
        Bind(DebugKnock, Godot.Key.F6);
        Bind(DebugListener, Godot.Key.F5);
        Bind(SummonWyrm, Godot.Key.F10);
        Bind(SummonWhale, Godot.Key.F4);
        Bind(DebugEye, Godot.Key.F3);
        Bind(Inventory, Godot.Key.I);
        Bind(IntakeToggle, Godot.Key.G);
        Bind(Dock, Godot.Key.K);
        Bind(Reload, Godot.Key.R);
        Bind(Flashlight, Godot.Key.F);
        Bind(UseMedkit, Godot.Key.H);

        Bind(Fullscreen, Godot.Key.F);
        Bind(ToggleFullscreenWindow, Godot.Key.F11);
    }

    private static void Bind(string action, params Godot.Key[] keys)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        foreach (var k in keys)
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
    }
}
