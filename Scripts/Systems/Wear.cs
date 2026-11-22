using System;
using System.Collections.Generic;
using System.Linq;

namespace Solitude.Systems;

public enum Part { IntakeFilter, HullSeals, Bearings, Scrubber, Electronics, Suit }

/// <summary>
/// THE BOAT AS A BODY (BALE IX: "a machine you learn to care for"). Six things wear out on their
/// own: through use, through depth, through weather and knocks. Each has a condition 0..100 and three
/// states (Good / Worn / Failing). The director turns states into symptoms (sounds, small failures)
/// and offers the fix at the part itself, by hand, with something from the hold.
/// Pure data and rates; saved.
/// </summary>
public sealed class Wear
{
    public enum State { Good, Worn, Failing }

    public sealed record Def(Part Part, string Name, string Room, (ItemId id, int n)[] Fix, string FixVerb,
        float Worn = 40f, float Failing = 15f);

    public static readonly Def[] All =
    {
        new(Part.IntakeFilter, "Intake filter", "ENGINEERING", new[] { (ItemId.Sediment, 0) }, "Clean the intake filter"),
        new(Part.HullSeals, "Hull seals", "QUARTERS", new[] { (ItemId.Sealant, 1) }, "Re-seal the hull seams"),
        new(Part.Bearings, "Drive bearings", "ENGINEERING", new[] { (ItemId.Plankton, 2) }, "Grease the drive bearings"),
        new(Part.Scrubber, "Air scrubber", "QUARTERS", new[] { (ItemId.Crystal, 1) }, "Swap the scrubber cartridge"),
        new(Part.Electronics, "Sensor bus", "SENSOR BAY", new[] { (ItemId.Circuit, 1) }, "Replace the sensor bus board"),
        new(Part.Suit, "Hard suit", "ENGINEERING", new[] { (ItemId.Sealant, 1) }, "Service the hard suit"),
    };

    public static Def Of(Part p) => All.First(d => d.Part == p);

    private readonly Dictionary<Part, float> _c = new();
    /// <summary>A part crossed into a worse state (for the log and a sound).</summary>
    public event Action<Part, State> Worsened;

    public Wear() { foreach (var d in All) _c[d.Part] = 100f; }

    public float Condition(Part p) => _c[p];

    public State StateOf(Part p)
    {
        var d = Of(p);
        float c = _c[p];
        return c <= d.Failing ? State.Failing : c <= d.Worn ? State.Worn : State.Good;
    }

    /// <summary>Wear a part by some amount (condition points).</summary>
    public void Use(Part p, float amount)
    {
        if (amount <= 0f) return;
        var before = StateOf(p);
        _c[p] = Math.Max(0f, _c[p] - amount);
        var after = StateOf(p);
        if (after > before) Worsened?.Invoke(p, after);
    }

    public void Restore(Part p) => _c[p] = 100f;

    /// <summary>Words, not numbers: the BOAT page reports how a part sounds, not a percentage.</summary>
    public static string Word(State s) => s switch { State.Good => "sound", State.Worn => "WORN", _ => "FAILING" };

    // ---- save
    public Dictionary<string, float> Save() => _c.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

    public void Load(Dictionary<string, float> d)
    {
        foreach (var def in All) _c[def.Part] = 100f;
        if (d == null) return;
        foreach (var (k, v) in d) if (Enum.TryParse<Part>(k, out var p)) _c[p] = Math.Clamp(v, 0f, 100f);
    }
}
