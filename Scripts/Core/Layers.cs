namespace Solitude.Core;

/// <summary>Physics layer bits. Keep in sync with any collision setup.</summary>
public static class Layers
{
    public const uint World = 1;      // seabed, rocks, spires
    public const uint Sub = 2;        // submarine hull (exterior)
    public const uint Interior = 4;   // walkable interior geometry and stations
    public const uint Player = 8;
}
