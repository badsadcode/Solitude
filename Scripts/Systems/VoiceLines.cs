using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Solitude.Systems;

/// <summary>
/// The voiced lines (assets/audio/voice/voice_lines.json, written by tools/voice/generate.py from
/// tools/voice/solitude_voice_script.json). Each line: a caption (exactly what's said, minus delivery
/// tags) and a WAV already treated to sound like it came through a radio. Loaded once, on demand.
/// Missing file = no voices: everything that uses this falls back to text.
/// </summary>
public static class VoiceLines
{
    public sealed record Line(string Id, string Caption, string File, float Seconds);

    private static Dictionary<string, Line> _lines;
    private static List<string> _subNames;
    private static int _capsules, _drones;

    private static void Load()
    {
        if (_lines != null) return;
        _lines = new();
        _subNames = new();
        const string path = "res://assets/audio/voice/voice_lines.json";
        if (!Godot.FileAccess.FileExists(path)) return;
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        using var doc = JsonDocument.Parse(f.GetAsText());
        var root = doc.RootElement;
        if (root.TryGetProperty("wrecks", out var w))
        {
            foreach (var n in w.GetProperty("supply_subs").EnumerateArray()) _subNames.Add(n.GetString());
            _capsules = w.GetProperty("capsules").GetInt32();
            _drones = w.GetProperty("drones").GetInt32();
        }
        foreach (var p in root.GetProperty("lines").EnumerateObject())
            _lines[p.Name] = new Line(p.Name, p.Value.GetProperty("caption").GetString(), p.Value.GetProperty("file").GetString(),
                p.Value.GetProperty("seconds").GetSingle());
    }

    public static Line Get(string id) { Load(); return _lines.TryGetValue(id, out var l) ? l : null; }
    public static bool Any { get { Load(); return _lines.Count > 0; } }
    public static IReadOnlyList<string> SubNames { get { Load(); return _subNames; } }
    public static int Capsules { get { Load(); return _capsules; } }
    public static int Drones { get { Load(); return _drones; } }
}
