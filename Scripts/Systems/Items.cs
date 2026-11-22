using System;
using System.Collections.Generic;

namespace Solitude.Systems;

public enum ItemId
{
    // Raw — collected outside
    Nodule, Crystal, Lumen, Clam, Plankton, Sediment,
    // Processed — workbench
    MetalPlate, Sealant, Circuit,
    // Equipment
    PatchKit, PowerCell,
    // Food — galley
    Ration, ClamStew, GlowBroth,
    // Found aboard stations
    CannedFood, Medkit, StationAlloy,
    // Tools
    MultiTool,
    // Salvaged components: only found aboard stations (modules, milestone 7)
    Servo, Valve, Coil, Optics, LogicBoard, Impeller,
    // Fabricated modules (fitted into the rack in engineering; see Modules.cs)
    ModFrameI, ModFrameII, ModAblative, ModBattery, ModThermal, ModLongSonar, ModFloods,
    ModHoldRacks, ModBilge, ModSuitTank, ModRebreather, ModReelMotor, ModCapacitor, ModExpander,
}

public sealed record ItemInfo(string Name, string Description, int Weight, float Food = 0f);

public static class Items
{
    public static readonly Dictionary<ItemId, ItemInfo> Info = new()
    {
        [ItemId.Nodule] = new("Manganese nodule", "Lumpy metal-rich concretion. Grew a millimetre per thousand years.", 2),
        [ItemId.Crystal] = new("Lattice crystal", "Faintly violet, faintly warm. Holds charge better than it should.", 1),
        [ItemId.Lumen] = new("Lumen pod", "Bioluminescent tissue. Still glowing. Still twitching.", 1),
        [ItemId.Clam] = new("Clam cluster", "Vent clams. Edible, probably.", 1),
        [ItemId.Plankton] = new("Plankton mass", "Filtered from the water column. Smells of copper.", 1),
        [ItemId.Sediment] = new("Sediment sample", "Fine grey ooze with trace minerals.", 1),
        [ItemId.MetalPlate] = new("Metal plate", "Pressed from nodules. Ugly. Strong.", 2),
        [ItemId.Sealant] = new("Bio-sealant", "Lumen gel thickened with sediment. Sets hard under pressure.", 1),
        [ItemId.Circuit] = new("Crystal circuit", "Hand-soldered. Mostly works.", 1),
        [ItemId.PatchKit] = new("Hull patch kit", "Plate + sealant. Restores hull integrity.", 2),
        [ItemId.PowerCell] = new("Power cell", "Crystal-lattice battery. Plug into the bus in engineering.", 2),
        [ItemId.Ration] = new("Plankton ration", "Grey bar. Tastes of nothing, which is the good part.", 1, 25f),
        [ItemId.ClamStew] = new("Clam stew", "Hot, salty, almost comforting.", 1, 50f),
        [ItemId.GlowBroth] = new("Glow broth", "Your mouth glows for an hour afterwards. You dream in blue.", 1, 35f),
        [ItemId.CannedFood] = new("Station tin", "Label long gone. Best before: the incident.", 1, 40f),
        [ItemId.MultiTool] = new("Multi-tool", "Arc welder, cutter, pry bar and torch in one battered housing. Needed to weld breaches.", 1),
        [ItemId.StationAlloy] = new("Station alloy", "Pressure-rated plate salvaged from a station. Nothing you can press from nodules is this strong.", 2),
        [ItemId.Medkit] = new("Medkit", "Gauze, sealant spray, something in an autoinjector. Press H to use.", 1),
        [ItemId.Servo] = new("Servo motor", "Station-grade actuator. Still twitches when you touch the contacts.", 1),
        [ItemId.Valve] = new("Pressure valve", "Rated for 2000 m. Somebody scratched a tally of days into the housing.", 1),
        [ItemId.Coil] = new("Induction coil", "Copper wound so tight it sings when power runs through it.", 1),
        [ItemId.Optics] = new("Sealed optic", "A pressure-proof lens. Something moves in the reflection that isn't behind you.", 1),
        [ItemId.LogicBoard] = new("Logic board", "Station controller. The firmware keeps a log you probably shouldn't read.", 1),
        [ItemId.Impeller] = new("Pump impeller", "Titanium vanes worn smooth. Hair tangled round the hub.", 1),
    };

    /// <summary>Module items take their names from the module table.</summary>
    static Items()
    {
        foreach (var m in Modules.All) Info[m.Item] = new($"{m.Name} (module)", m.Effect, 2);
    }

    public static string Name(ItemId id) => Info[id].Name;
}

/// <summary>The boat's hold. Weight-limited.</summary>
public sealed class Inventory
{
    private readonly Dictionary<ItemId, int> _counts = new();
    public int Capacity = 40;
    public int Version { get; private set; }

    public int Count(ItemId id) => _counts.TryGetValue(id, out int c) ? c : 0;

    public void Clear() { _counts.Clear(); Version++; }

    /// <summary>Restore from a save (ignores the weight limit: the save was valid when written).</summary>
    public void Set(ItemId id, int n) { if (n > 0) _counts[id] = n; else _counts.Remove(id); Version++; }

    public int Weight
    {
        get
        {
            int w = 0;
            foreach (var kv in _counts) w += kv.Value * Items.Info[kv.Key].Weight;
            return w;
        }
    }

    public bool CanFit(ItemId id, int n = 1) => Weight + Items.Info[id].Weight * n <= Capacity;

    public bool Add(ItemId id, int n = 1)
    {
        if (!CanFit(id, n)) return false;
        _counts[id] = Count(id) + n;
        Version++;
        return true;
    }

    public bool Remove(ItemId id, int n = 1)
    {
        if (Count(id) < n) return false;
        _counts[id] = Count(id) - n;
        if (_counts[id] == 0) _counts.Remove(id);
        Version++;
        return true;
    }

    public bool Has(IReadOnlyList<(ItemId id, int n)> cost)
    {
        foreach (var (id, n) in cost) if (Count(id) < n) return false;
        return true;
    }

    public IEnumerable<(ItemId id, int count)> All()
    {
        foreach (ItemId id in Enum.GetValues<ItemId>())
            if (Count(id) > 0) yield return (id, Count(id));
    }
}

public enum Station { Workbench, Galley }

public sealed record Recipe(Station Station, ItemId Output, int OutputCount, (ItemId id, int n)[] Cost, float PowerCost, string Verb);

public static class Recipes
{
    public static readonly Recipe[] All =
    {
        new(Station.Workbench, ItemId.MetalPlate, 1, new[] { (ItemId.Nodule, 2) }, 2f, "Press"),
        new(Station.Workbench, ItemId.Sealant, 1, new[] { (ItemId.Lumen, 2), (ItemId.Sediment, 1) }, 1f, "Mix"),
        new(Station.Workbench, ItemId.Circuit, 1, new[] { (ItemId.Crystal, 2), (ItemId.MetalPlate, 1) }, 2f, "Solder"),
        new(Station.Workbench, ItemId.PatchKit, 1, new[] { (ItemId.MetalPlate, 2), (ItemId.Sealant, 1) }, 1f, "Assemble"),
        new(Station.Workbench, ItemId.PowerCell, 1, new[] { (ItemId.Circuit, 1), (ItemId.Crystal, 2) }, 3f, "Assemble"),
        new(Station.Galley, ItemId.Ration, 1, new[] { (ItemId.Plankton, 3) }, 1f, "Press"),
        new(Station.Galley, ItemId.ClamStew, 1, new[] { (ItemId.Clam, 2), (ItemId.Plankton, 1) }, 1.5f, "Cook"),
        new(Station.Galley, ItemId.GlowBroth, 1, new[] { (ItemId.Lumen, 2), (ItemId.Plankton, 1) }, 1.5f, "Simmer"),
    };

    public static string CostText(Recipe r)
    {
        var parts = new List<string>();
        foreach (var (id, n) in r.Cost) parts.Add($"{n}x {Items.Name(id)}");
        return string.Join(" + ", parts);
    }
}
