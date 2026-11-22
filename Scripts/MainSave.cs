using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Solitude.Player;
using Solitude.Stations;
using Solitude.Systems;

namespace Solitude;

/// <summary>Everything needed to resume a dive. Plain JSON in user://save.json.</summary>
public sealed class SaveData
{
    public int Version = 1;
    public int Seed;
    public string SavedAt;
    public float[] SubPos, SubRot;
    public float Hull, Power, Food, Health;
    public bool SuitOn, OriginKnown, Ended;
    public Dictionary<string, int> Hold = new();
    public List<string> Upgrades = new();
    public List<long> CoreSites = new();
    public List<string> WeaponsOwned = new();
    public Dictionary<string, int> Mag = new(), Reserve = new();
    public List<long> KnownStations = new();
    public List<long> TakenPickups = new();
    public List<StationSave> Stations = new();
}

public sealed class StationSave
{
    public long Id;
    public List<int> Taken = new(), Killed = new();
    public bool DoorOpen, SecretOpen, HasKeycard, Visited;
}

/// <summary>
/// Save and Continue (milestone 6). Autosaves at natural moments: docking, coming back aboard,
/// returning from a dive, a data core, an upgrade, the ending, and when the window closes.
/// CONTINUE reloads the scene with the saved seed and re-applies the state on top.
/// </summary>
public partial class Main
{
    public const string SavePath = "user://save.json";
    /// <summary>Set by CONTINUE before the scene reloads; consumed in _Ready.</summary>
    public static SaveData PendingLoad;

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static bool HasSave() => Godot.FileAccess.FileExists(SavePath);

    private static SaveData ReadSave()
    {
        try
        {
            using var f = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read);
            return f == null ? null : JsonSerializer.Deserialize<SaveData>(f.GetAsText(), Json);
        }
        catch (Exception e) { GD.PushWarning($"[save] unreadable save: {e.Message}"); return null; }
    }

    private bool _saveBlocked => _captureDir != null || _titleCaptureDir != null || _mode == Mode.Title;

    private void Autosave(string reason)
    {
        if (_saveBlocked) return;
        try
        {
            var d = Capture();
            using var f = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(d, Json));
            GD.Print($"[save] {reason} -> {ProjectSettings.GlobalizePath(SavePath)}");
        }
        catch (Exception e) { GD.PushError($"[save] failed: {e.Message}"); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Autosave("quit");
    }

    private SaveData Capture()
    {
        var q = _sub.GlobalBasis.GetRotationQuaternion();
        var p = _sub.GlobalPosition;
        // If docked, save the boat just astern of the berth so it doesn't load inside the clamps.
        if (_stations.DockedAt != null) p = _stations.DockedAt.DockPose.Origin + _stations.DockedAt.DockPose.Basis.Z * 14f;
        var d = new SaveData
        {
            Seed = Seed,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            SubPos = new[] { p.X, p.Y, p.Z },
            SubRot = new[] { q.X, q.Y, q.Z, q.W },
            Hull = _sub.Hull, Power = _systems.Power, Food = _systems.Food,
            Health = Mathf.Max(_player.Health, 30f),
            SuitOn = _suitOn, OriginKnown = _originKnown, Ended = _ended,
            Upgrades = _upgrades.Installed.Select(u => u.ToString()).ToList(),
            CoreSites = _coreSites.ToList(),
            WeaponsOwned = _weapons.Owned.Select(w => w.ToString()).ToList(),
            Mag = _weapons.Mag.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            Reserve = _weapons.Reserve.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            TakenPickups = _terrain.TakenIds.ToList(),
        };
        foreach (var (id, count) in _systems.Hold.All()) d.Hold[id.ToString()] = count;
        foreach (var s in _stations.Directory.Generated) if (s.Known) d.KnownStations.Add(s.Id);
        foreach (var (id, st) in _stations.States)
            d.Stations.Add(new StationSave
            {
                Id = id, Taken = st.Taken.ToList(), Killed = st.Killed.ToList(),
                DoorOpen = st.DoorOpen, SecretOpen = st.SecretOpen, HasKeycard = st.HasKeycard, Visited = st.Visited,
            });
        return d;
    }

    /// <summary>Re-apply a save on top of a freshly built world with the same seed.</summary>
    private void ApplySave(SaveData d)
    {
        foreach (var u in d.Upgrades) if (Enum.TryParse<UpgradeId>(u, out var id)) _upgrades.Installed.Add(id);
        ApplyUpgrades();

        _systems.Hold.Clear();
        foreach (var (k, n) in d.Hold) if (Enum.TryParse<ItemId>(k, out var id)) _systems.Hold.Set(id, n);
        _systems.SetState(d.Power, d.Food);
        _sub.SetHull(d.Hull);
        _player.Revive(d.Health);
        _suitOn = d.SuitOn;
        _player.SpeedFactor = _suitOn ? 0.7f : 1f;

        foreach (var w in d.WeaponsOwned) if (Enum.TryParse<WeaponId>(w, out var id)) _weapons.Owned.Add(id);
        foreach (var (k, n) in d.Mag) if (Enum.TryParse<WeaponId>(k, out var id)) _weapons.Mag[id] = n;
        foreach (var (k, n) in d.Reserve) if (Enum.TryParse<WeaponId>(k, out var id)) _weapons.Reserve[id] = n;

        foreach (var s in d.Stations)
        {
            var st = new StationState { DoorOpen = s.DoorOpen, SecretOpen = s.SecretOpen, HasKeycard = s.HasKeycard, Visited = s.Visited };
            foreach (var i in s.Taken) st.Taken.Add(i);
            foreach (var i in s.Killed) st.Killed.Add(i);
            _stations.States[s.Id] = st;
        }
        foreach (var id in d.KnownStations) { var site = _stations.Directory.ById(id); if (site != null) site.Known = true; }

        foreach (var id in d.CoreSites) _coreSites.Add(id);
        _originKnown = d.OriginKnown;
        _ended = d.Ended;
        _audio.Unease = Mathf.Min(_coreSites.Count, CoresNeeded) / (float)CoresNeeded;

        var pos = new Vector3(d.SubPos[0], d.SubPos[1], d.SubPos[2]);
        var rot = new Quaternion(d.SubRot[0], d.SubRot[1], d.SubRot[2], d.SubRot[3]);
        _terrain.GenerateImmediate(pos, 2);
        _terrain.RestoreTaken(d.TakenPickups);
        _sub.GlobalTransform = new Transform3D(new Basis(rot), pos);
        _sub.LinearVelocity = Vector3.Zero;
        _sub.AngularVelocity = Vector3.Zero;
        GD.Print($"[save] continued dive saved {d.SavedAt} (seed {d.Seed})");
    }

    /// <summary>Title → CONTINUE: rebuild the world for the saved seed and resume.</summary>
    private void ContinueDive()
    {
        var d = ReadSave();
        if (d == null) { _hud.Message("The save could not be read."); return; }
        PendingLoad = d;
        StopTitleMusic(1.0f);
        _hud.HideTitle();
        _hud.FadeTo(1f, 1f);
        var tw = CreateTween();
        tw.TweenInterval(1.1);
        tw.TweenCallback(Callable.From(() => GetTree().ReloadCurrentScene()));
    }
}
