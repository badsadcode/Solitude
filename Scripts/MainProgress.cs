using System.Collections.Generic;
using Godot;
using Solitude.Systems;
using Solitude.World;

namespace Solitude;

/// <summary>
/// Milestone 6/7 glue: modules (fabricate, fit, blueprints), biome arrival notices, and the story's
/// progression (data cores → the Origin → the ending). Save/load lives in MainSave.cs.
/// </summary>
public partial class Main
{
    private readonly ModuleState _modules = new();
    private Biome _currentBiome = Biome.Shelf;
    private float _biomeCheck;

    // ------------------------------------------------------------------ modules

    /// <summary>Workbench: turn components into a module (it goes into the hold, ready to fit).</summary>
    private void FabricateModule(ModuleDef m)
    {
        var hold = _systems.Hold;
        if (!hold.Has(m.Cost)) { _hud.Message("Missing components."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        if (!_systems.UsePower(4f)) { _hud.Message("Not enough power to run the fabricator."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        foreach (var (id, n) in m.Cost) hold.Remove(id, n);
        hold.Set(m.Item, hold.Count(m.Item) + 1);   // a module always fits: the parts it ate weighed more
        _audio.PlayImpact(0.35f);
        _audio.PlayAt("weld", 0f, 0.6f);
        _hud.Message($"FABRICATED: {m.Name}. Fit it in the module rack (engineering).", 5f);
    }

    /// <summary>Engineering: put a module from the hold into a rack bay.</summary>
    private void FitModule(ModuleDef m)
    {
        if (_systems.Hold.Count(m.Item) == 0) return;
        if (_modules.WhyNot(m) is string why) { _hud.Message($"Can't fit {m.Name}: {why}."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        if (_modules.Displaces(m) is ModuleId old)
        {
            _modules.Fitted.Remove(old);
            _systems.Hold.Set(Modules.Def(old).Item, _systems.Hold.Count(Modules.Def(old).Item) + 1);
        }
        _systems.Hold.Remove(m.Item);
        _modules.Fitted.Add(m.Id);
        ApplyModules();
        _audio.PlayImpact(0.6f);
        _audio.PlayAt("door", 0f, 0.5f);
        _hud.Message($"FITTED: {m.Name}. {char.ToUpper(m.Effect[0])}{m.Effect.Substring(1)}.", 5f);
        Autosave("module");
    }

    /// <summary>Engineering: pull a module back out into the hold.</summary>
    private void RemoveModule(ModuleDef m)
    {
        if (!_modules.Has(m.Id)) return;
        if (m.Id == ModuleId.Expander && _modules.Used > Modules.BaseBays) { _hud.Message("Empty the two extra bays before pulling the expander."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        if (m.Id == ModuleId.HoldRacks && _systems.Hold.Weight > 40 - 2) { _hud.Message("The hold is too full to take the racks out."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        bool deep = m.Group == "frame" && _sub.Depth > 750f - 20f;
        if (deep) { _hud.Message("Pull the pressure frame at THIS depth? The hull would fold. Surface above 730 m first."); _audio.PlayAt("deny", 0f, 0.6f); return; }
        _modules.Fitted.Remove(m.Id);
        _systems.Hold.Set(m.Item, _systems.Hold.Count(m.Item) + 1);
        ApplyModules();
        _audio.PlayAt("door", 0f, 0.4f);
        _hud.Message($"REMOVED: {m.Name}. Stowed in the hold.", 4f);
    }

    /// <summary>A blueprint datapad from a station. Core datapads follow a fixed order (the deep frame first).</summary>
    private void OnBlueprint(bool core)
    {
        ModuleId? next = null;
        if (core) foreach (var id in Modules.CoreOrder) if (!_modules.Known.Contains(id)) { next = id; break; }
        if (next == null)
        {
            var unknown = new System.Collections.Generic.List<ModuleId>();
            foreach (var m in Modules.All) if (!_modules.Known.Contains(m.Id)) unknown.Add(m.Id);
            if (unknown.Count > 0) next = unknown[(int)(GD.Randi() % (uint)unknown.Count)];
        }
        if (next is ModuleId id2)
        {
            _modules.Known.Add(id2);
            var def = Modules.Def(id2);
            _audio.PlayAt("pickup", 0f, 0.6f);
            _hud.Message($"BLUEPRINT: {def.Name}. {char.ToUpper(def.Effect[0])}{def.Effect.Substring(1)}. Needs {Modules.CostText(def)}.", 7f);
        }
        else
        {
            // Nothing new on it: strip the pad for its board.
            _systems.Collect(ItemId.LogicBoard, announce: false);
            _hud.Message("Nothing on this pad you don't already know. You pry out its logic board.", 4f);
        }
    }

    /// <summary>Push every fitted module's effect into the live systems (also after loading).</summary>
    private void ApplyModules()
    {
        bool f1 = _modules.Has(ModuleId.FrameI), f2 = _modules.Has(ModuleId.FrameII);
        _sub.TestDepth = f2 ? 1200f : f1 ? 950f : 750f;
        _sub.CrushDepth = f2 ? 1600f : f1 ? 1250f : 1050f;
        bool abl = _modules.Has(ModuleId.Ablative);
        _sub.DamageScale = abl ? 0.6f : 1f;
        if (_hull != null)
        {
            _hull.BreachScale = abl ? 0.4f : 1f;
            _hull.PumpRate = _modules.Has(ModuleId.Bilge) ? 0.27f : 0.09f;
        }
        _systems.BatteryCapacity = _modules.Has(ModuleId.Battery) ? 1.5f : 1f;
        bool th = _modules.Has(ModuleId.Thermal);
        _systems.VentRange = th ? 70f : 40f;
        _systems.VentRate = th ? 3f : 1f;
        _systems.Hold.Capacity = _modules.Has(ModuleId.HoldRacks) ? 60 : 40;
        _diver.TankMinutes = _modules.Has(ModuleId.SuitTank) ? 6.5f : 4f;
        _diver.ExertionAir = _modules.Has(ModuleId.Rebreather) ? 0.35f : 1f;
        _sonar.LongRange = _modules.Has(ModuleId.LongSonar);
        _sub.SetFloodBoost(_modules.Has(ModuleId.Floods) ? 1.4f : 1f);
        if (_harpoon != null)
        {
            bool reel = _modules.Has(ModuleId.ReelMotor), cap = _modules.Has(ModuleId.Capacitor);
            _harpoon.LineLength = reel ? 40f : 25f;
            _harpoon.ReelSpeed = reel ? 22f : 11f;
            _harpoon.ProdRecharge = cap ? 1.2f : 3f;
            _harpoon.SelfZapChance = cap ? 0f : 0.12f;
        }
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
