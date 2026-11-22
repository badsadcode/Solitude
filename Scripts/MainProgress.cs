using System.Collections.Generic;
using Godot;
using Solitude.Systems;
using Solitude.World;

namespace Solitude;

/// <summary>
/// Milestone 6 glue: upgrades, biome arrival notices, and the story's progression
/// (data cores → the Origin → the ending). Save/load lives in MainSave.cs.
/// </summary>
public partial class Main
{
    private readonly UpgradeState _upgrades = new();
    private Biome _currentBiome = Biome.Shelf;
    private float _biomeCheck;

    // ------------------------------------------------------------------ upgrades

    private void InstallUpgrade(UpgradeDef u)
    {
        if (!_upgrades.CanInstall(u, _systems.Hold)) { _hud.Message("Missing parts — or a prerequisite."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        foreach (var (id, n) in u.Cost) _systems.Hold.Remove(id, n);
        _upgrades.Installed.Add(u.Id);
        ApplyUpgrades();
        _audio.PlayImpact(0.6f);
        _audio.PlayAt("door", 0f, 0.5f);
        _hud.Message($"INSTALLED: {u.Name}. {char.ToUpper(u.Effect[0])}{u.Effect.Substring(1)}.", 5f);
        Autosave("upgrade");
    }

    /// <summary>Push every installed upgrade's effect into the live systems (also after loading).</summary>
    private void ApplyUpgrades()
    {
        bool h1 = _upgrades.Has(UpgradeId.HullI), h2 = _upgrades.Has(UpgradeId.HullII);
        _sub.TestDepth = h2 ? 1200f : h1 ? 950f : 750f;
        _sub.CrushDepth = h2 ? 1600f : h1 ? 1250f : 1050f;
        _systems.BatteryCapacity = _upgrades.Has(UpgradeId.Battery) ? 1.5f : 1f;
        _systems.Hold.Capacity = _upgrades.Has(UpgradeId.HoldRack) ? 60 : 40;
        _diver.TankMinutes = _upgrades.Has(UpgradeId.SuitTank) ? 6.5f : 4f;
        _sonar.LongRange = _upgrades.Has(UpgradeId.LongSonar);
        _sub.SetFloodBoost(_upgrades.Has(UpgradeId.Floods) ? 1.4f : 1f);
    }

    // ------------------------------------------------------------------ biomes

    private void UpdateBiomeNotice(float dt)
    {
        if (_mode == Mode.Title) return;
        _biomeCheck -= dt;
        if (_biomeCheck > 0f) return;
        _biomeCheck = 2f;
        Vector3 f = FocusPoint();
        var b = _gen.BiomeAt(f.X, f.Z);
        if (TerrainGenerator.HadalFactor(f.Y) > 0.6f) b = Biome.Hadal;
        if (b == _currentBiome || b == Biome.Vents) return;
        _currentBiome = b;
        _hud.Message(b switch
        {
            Biome.Crystal => "THE CRYSTAL EXPANSE. The water turns violet. Something in the rock is awake.",
            Biome.Hadal => "THE HADAL DEEP. The dark here has a colour, and it is red.",
            _ => "THE PALE SHELF.",
        }, 6f);
    }
}
