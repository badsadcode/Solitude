namespace Solitude.Core;

/// <summary>Physics layer bits. Keep in sync with any collision setup.</summary>
public static class Layers
{
    public const uint World = 1;      // seabed, rocks, spires
    public const uint Sub = 2;        // submarine hull (exterior)
    public const uint Interior = 4;   // walkable interior geometry and stations
    public const uint Player = 8;
    public const uint Enemy = 16;     // station creatures (hit by weapons, not by the player capsule)
    public const uint Use = 32;       // interaction volumes: seen by the use-ray only (not bullets, not bodies)
}
