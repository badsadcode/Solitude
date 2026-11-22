using System.Collections.Generic;
using System.Linq;

namespace Solitude.Systems;

public enum ModuleId { FrameI, FrameII, Ablative, Battery, Thermal, LongSonar, Floods, HoldRacks, Bilge, SuitTank, Rebreather, ReelMotor, Capacitor, Expander }

/// <summary>
/// One module. Bays = rack space it takes; Group = only one of the group fitted at a time
/// (fitting Mk II swaps Mk I back into the hold).
/// </summary>
public sealed record ModuleDef(ModuleId Id, ItemId Item, string Name, string Effect, (ItemId id, int n)[] Cost, int Bays = 1, string Group = null);

/// <summary>
/// Boat and suit modules (Tom: "upgrading the submarine with modules which could be found in parts
/// and crafted at the workstation").
///
/// The loop: salvage COMPONENTS in stations (servos, valves, coils, optics, logic boards, impellers),
/// find BLUEPRINTS on station datapads (every station core holds one), FABRICATE the module at the
/// workbench, then FIT it into the module rack in engineering. The rack has only four bays (six with
/// the expander), so a loadout is a choice: going deep, staying out longer, or fighting back.
/// Modules can be pulled and swapped at any time; they go back into the hold.
/// </summary>
public static class Modules
{
    public static readonly ModuleDef[] All =
    {
        new(ModuleId.FrameI, ItemId.ModFrameI, "Pressure frame Mk I", "test depth 950 m, crush depth 1250 m",
            new[] { (ItemId.MetalPlate, 4), (ItemId.StationAlloy, 2), (ItemId.Valve, 1) }, Group: "frame"),
        new(ModuleId.FrameII, ItemId.ModFrameII, "Pressure frame Mk II", "test depth 1200 m, crush depth 1600 m",
            new[] { (ItemId.StationAlloy, 4), (ItemId.MetalPlate, 3), (ItemId.Valve, 2), (ItemId.Circuit, 2) }, Group: "frame"),
        new(ModuleId.Ablative, ItemId.ModAblative, "Ablative skin", "hull damage -40%, breaches far rarer",
            new[] { (ItemId.MetalPlate, 4), (ItemId.Sealant, 2), (ItemId.StationAlloy, 1) }),
        new(ModuleId.Battery, ItemId.ModBattery, "Battery bank", "power lasts 50% longer",
            new[] { (ItemId.PowerCell, 2), (ItemId.Circuit, 1), (ItemId.Coil, 1) }),
        new(ModuleId.Thermal, ItemId.ModThermal, "Thermal siphon", "vents charge 3x faster, from 70 m",
            new[] { (ItemId.Coil, 2), (ItemId.Circuit, 1), (ItemId.MetalPlate, 2) }),
        new(ModuleId.LongSonar, ItemId.ModLongSonar, "Long-range transducer", "sonar gains an 800 m range",
            new[] { (ItemId.Circuit, 2), (ItemId.Crystal, 2), (ItemId.Optics, 1) }),
        new(ModuleId.Floods, ItemId.ModFloods, "Flood array", "floodlights reach 40% further",
            new[] { (ItemId.Circuit, 1), (ItemId.Crystal, 2), (ItemId.Optics, 1) }),
        new(ModuleId.HoldRacks, ItemId.ModHoldRacks, "Hold racks", "hold capacity 40 -> 60",
            new[] { (ItemId.MetalPlate, 3), (ItemId.StationAlloy, 1), (ItemId.Servo, 1) }),
        new(ModuleId.Bilge, ItemId.ModBilge, "Bilge pumps", "floodwater drains 3x faster",
            new[] { (ItemId.Impeller, 2), (ItemId.Servo, 1), (ItemId.MetalPlate, 1) }),
        new(ModuleId.SuitTank, ItemId.ModSuitTank, "Suit tank", "air 4 -> 6.5 minutes",
            new[] { (ItemId.StationAlloy, 1), (ItemId.Sealant, 1), (ItemId.Valve, 1) }),
        new(ModuleId.Rebreather, ItemId.ModRebreather, "Rebreather", "swimming hard costs far less air",
            new[] { (ItemId.Valve, 1), (ItemId.Crystal, 2), (ItemId.Sealant, 1) }),
        new(ModuleId.ReelMotor, ItemId.ModReelMotor, "Reel motor", "harpoon line 25 -> 40 m, reels twice as fast",
            new[] { (ItemId.Servo, 1), (ItemId.Coil, 1), (ItemId.MetalPlate, 1) }),
        new(ModuleId.Capacitor, ItemId.ModCapacitor, "Prod capacitor", "shock prod recharges in 1.2 s and never bites you",
            new[] { (ItemId.Coil, 2), (ItemId.Crystal, 2), (ItemId.LogicBoard, 1) }),
        new(ModuleId.Expander, ItemId.ModExpander, "Rack expander", "+2 module bays (takes none itself)",
            new[] { (ItemId.StationAlloy, 3), (ItemId.LogicBoard, 2), (ItemId.Servo, 1) }, Bays: 0),
    };

    /// <summary>Blueprints you start with: the boat's own refit manual.</summary>
    public static readonly ModuleId[] Starting = { ModuleId.FrameI, ModuleId.HoldRacks, ModuleId.SuitTank, ModuleId.Battery };

    /// <summary>Station cores hand out blueprints in this order, so the deep frame comes early.</summary>
    public static readonly ModuleId[] CoreOrder =
        { ModuleId.FrameII, ModuleId.Thermal, ModuleId.LongSonar, ModuleId.Ablative, ModuleId.ReelMotor, ModuleId.Bilge, ModuleId.Rebreather, ModuleId.Expander, ModuleId.Capacitor, ModuleId.Floods };

    public const int BaseBays = 4;

    public static ModuleDef Def(ModuleId id) => All.First(m => m.Id == id);
    public static ModuleDef ByItem(ItemId item) => All.FirstOrDefault(m => m.Item == item);

    public static string CostText(ModuleDef m) => string.Join(" + ", m.Cost.Select(c => $"{c.n}x {Items.Name(c.id)}"));
}

/// <summary>What's fitted and which blueprints are known. Main.ApplyModules() pushes the effects.</summary>
public sealed class ModuleState
{
    public readonly List<ModuleId> Fitted = new();
    public readonly HashSet<ModuleId> Known = new(Modules.Starting);

    public bool Has(ModuleId id) => Fitted.Contains(id);
    public int Bays => Modules.BaseBays + (Has(ModuleId.Expander) ? 2 : 0);
    public int Used => Fitted.Sum(m => Modules.Def(m).Bays);

    /// <summary>The fitted module this one would replace (same group), if any.</summary>
    public ModuleId? Displaces(ModuleDef m) =>
        m.Group == null ? null : Fitted.Where(f => f != m.Id && Modules.Def(f).Group == m.Group).Select(f => (ModuleId?)f).FirstOrDefault();

    /// <summary>Null if it can be fitted, else the reason it can't.</summary>
    public string WhyNot(ModuleDef m)
    {
        if (Has(m.Id)) return "already fitted";
        int freed = Displaces(m) is ModuleId d ? Modules.Def(d).Bays : 0;
        if (Used - freed + m.Bays > Bays) return "rack full";
        return null;
    }
}
