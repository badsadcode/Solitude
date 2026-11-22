using Godot;

namespace Solitude.Systems;

/// <summary>Player preferences, kept apart from save games in user://settings.cfg.</summary>
public static class Settings
{
    private const string Path = "user://settings.cfg";

    public static float MasterVolume = 1f;      // 0..1
    public static float MusicVolume = 1f;       // 0..1 (title music)
    public static float MouseSensitivity = 1f;  // multiplier, 0.3..2
    public static bool Fullscreen;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) { Apply(); return; }
        MasterVolume = (float)cfg.GetValue("audio", "master", 1f);
        MusicVolume = (float)cfg.GetValue("audio", "music", 1f);
        MouseSensitivity = (float)cfg.GetValue("input", "mouse", 1f);
        Fullscreen = (bool)cfg.GetValue("video", "fullscreen", false);
        Apply();
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "master", MasterVolume);
        cfg.SetValue("audio", "music", MusicVolume);
        cfg.SetValue("input", "mouse", MouseSensitivity);
        cfg.SetValue("video", "fullscreen", Fullscreen);
        cfg.Save(Path);
    }

    public static void Apply()
    {
        AudioServer.SetBusVolumeDb(0, MasterVolume <= 0.001f ? -80f : Mathf.LinearToDb(MasterVolume));
        DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
    }

    /// <summary>Music sits on top of its own base level.</summary>
    public static float MusicDb(float baseDb) => MusicVolume <= 0.001f ? -80f : baseDb + Mathf.LinearToDb(MusicVolume);
}
