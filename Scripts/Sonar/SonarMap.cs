using System;
using System.Collections.Generic;
using Godot;

namespace Solitude.Sonar;

/// <summary>
/// Persistent memory of every seabed cell the sonar has ever "seen" (8 m grid).
/// The holo table renders from this; later, map fragments found in stations will
/// write into it too. Main thread only.
/// </summary>
public sealed class SonarMap
{
    public const float Cell = 8f;

    private readonly Dictionary<Vector2I, (float height, double revealAt)> _cells = new();

    /// <summary>Bumped on every change so renderers know when to rebuild.</summary>
    public int Version { get; private set; }
    public int Count => _cells.Count;

    public static Vector2I CellOf(float x, float z) => new((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));
    public static Vector2 CellCentre(Vector2I c) => new((c.X + 0.5f) * Cell, (c.Y + 0.5f) * Cell);

    /// <summary>Add cells; revealAt is when the sonar wavefront reaches them (for animated reveal).</summary>
    public void Reveal(IEnumerable<(Vector2I cell, float height, double revealAt)> cells)
    {
        foreach (var (cell, h, t) in cells)
        {
            // Keep the earliest reveal time, refresh the height.
            if (_cells.TryGetValue(cell, out var old)) _cells[cell] = (h, Math.Min(old.revealAt, t));
            else _cells[cell] = (h, t);
        }
        Version++;
    }

    public bool TryGet(Vector2I cell, out float height, out double revealAt)
    {
        if (_cells.TryGetValue(cell, out var v)) { height = v.height; revealAt = v.revealAt; return true; }
        height = 0; revealAt = 0;
        return false;
    }
}
