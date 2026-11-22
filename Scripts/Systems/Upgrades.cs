using System.Collections.Generic;

namespace Solitude.Systems;

public enum UpgradeId { HullI, HullII, Battery, HoldRack, SuitTank, LongSonar, Floods }

public sealed record UpgradeDef(UpgradeId Id, string Name, string Effect, (ItemId id, int n)[] Cost, UpgradeId? Requires = null);

/// <summary>
/// Boat and suit upgrades, installed at the engineering panel. They cost crafted parts plus
/// station alloy (salvage), which pulls you into stations. Hull Reinforcement is the gate to
/// the deep: the Origin sits at the bottom of a shaft far below stock crush depth.
/// </summary>
public static class Upgrades
{
    public static readonly UpgradeDef[] All =
    {
        new(UpgradeId.HullI, "Hull reinforcement I", "test depth 950 m, crush depth 1250 m",
            new[] { (ItemId.MetalPlate, 4), (ItemId.StationAlloy, 2), (ItemId.Sealant, 1) }),
        new(UpgradeId.HullII, "Hull reinforcement II", "test depth 1200 m, crush depth 1600 m",
            new[] { (ItemId.StationAlloy, 4), (ItemId.MetalPlate, 3), (ItemId.Circuit, 2) }, UpgradeId.HullI),
        new(UpgradeId.Battery, "Battery bank", "power lasts 50% longer",
            new[] { (ItemId.PowerCell, 2), (ItemId.Circuit, 2), (ItemId.StationAlloy, 1) }),
        new(UpgradeId.HoldRack, "Hold racks", "hold capacity 40 → 60",
            new[] { (ItemId.MetalPlate, 3), (ItemId.StationAlloy, 1) }),
        new(UpgradeId.SuitTank, "Suit tank", "air 4 → 6.5 minutes",
            new[] { (ItemId.StationAlloy, 2), (ItemId.Sealant, 1), (ItemId.Crystal, 1) }),
        new(UpgradeId.LongSonar, "Long-range transducer", "sonar gains an 800 m range",
            new[] { (ItemId.Circuit, 2), (ItemId.Crystal, 3) }),
        new(UpgradeId.Floods, "Flood array", "floodlights reach 40% further",
            new[] { (ItemId.Circuit, 2), (ItemId.Crystal, 2), (ItemId.MetalPlate, 1) }),
    };

    public static UpgradeDef Def(UpgradeId id) { foreach (var u in All) if (u.Id == id) return u; return null; }
}

/// <summary>What's installed. Apply() pushes every effect into the live systems.</summary>
public sealed class UpgradeState
{
    public readonly HashSet<UpgradeId> Installed = new();

    public bool Has(UpgradeId id) => Installed.Contains(id);

    public bool CanInstall(UpgradeDef u, Inventory hold) =>
        !Has(u.Id) && (u.Requires == null || Has(u.Requires.Value)) && hold.Has(u.Cost);
}
