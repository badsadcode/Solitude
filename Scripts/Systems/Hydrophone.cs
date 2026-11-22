using System;
using System.Collections.Generic;

namespace Solitude.Systems;

/// <summary>What made a sound. The operator never sees this directly, only its signature.</summary>
public enum SoundClass { Ravager, Wyrm, Ray, Vent, Station, Listener, Sounding, Geology, Whale }

/// <summary>
/// A sound's fingerprint: how much energy in the low / mid / high bands, whether it pulses (and
/// how fast, and how long each pulse lasts), and an optional steady tone. The headphones
/// synthesise from this; the console describes it in words; recordings are matched by class.
/// </summary>
public readonly record struct Signature(float Low, float Mid, float High, float PulseHz, float Duty, float ToneHz, float Tone);

/// <summary>One sound arriving at the boat: relative bearing (deg, 0 = bow, +starboard), level 0..1, spread (deg).</summary>
public struct HydroSource
{
    public SoundClass Class;
    public float Bearing;
    public float Level;
    public float Spread;
    public float Distance;
}

public sealed record Recording(int No, SoundClass Class, float TrueBearing, float Depth, bool Identified);

/// <summary>
/// THE HYDROPHONE (an idea from Thalassa): the passive listening console, the other half of the
/// Listener. It hunts you by sound; this is how you hunt it back.
///
/// A bearing-time "waterfall": every 0.4 s a new row of 72 bearing bins (5° each, ±180° around the
/// bow) scrolls in at the top. A sound source paints a bright track at its bearing, so moving
/// things draw slanted lines and still things draw straight ones. Your own noise is the enemy:
/// it lifts the floor on every bearing (most of all astern, where the screw is). Go quiet to listen.
///
/// Recording: hold the beam on a sound for a few seconds and the console stores its signature.
/// Later sounds of the same kind are flagged as MATCH REC nn. Recording something you can actually
/// see (or a station you're right next to) identifies it by name. One signature is never
/// identified: the one from the Origin, which is there from the first minute, very faint, counting.
/// </summary>
public sealed class Hydrophone
{
    public const int Bins = 72, Rows = 100;
    public const float RowInterval = 0.4f, BinDeg = 360f / Bins;

    public enum Band { All, Low, Mid, High }
    public Band Filter = Band.All;

    /// <summary>Intensity history, [row, bin], row 0 = newest.</summary>
    public readonly float[,] Waterfall = new float[Rows, Bins];
    public int Version { get; private set; }

    public readonly List<Recording> Recordings = new();
    public readonly HashSet<SoundClass> Identified = new();
    public const int MaxRecordings = 12;

    private float _rowT;
    private readonly Random _rng = new();

    public static Signature Of(SoundClass c) => c switch
    {
        SoundClass.Ravager => new(0.25f, 0.8f, 0.55f, 3.2f, 0.25f, 0f, 0f),     // sharp clicks, quick
        SoundClass.Wyrm => new(0.9f, 0.45f, 0.1f, 0.35f, 0.6f, 0f, 0f),        // a long, slow sweep
        SoundClass.Ray => new(0.05f, 0.3f, 0.85f, 1.6f, 0.35f, 0f, 0f),        // high chirps
        SoundClass.Vent => new(0.5f, 0.55f, 0.45f, 0f, 1f, 0f, 0f),            // broadband roar, steady
        SoundClass.Station => new(0.55f, 0.25f, 0.1f, 0f, 1f, 50f, 0.6f),      // machinery hum (or its silence)
        SoundClass.Listener => new(1f, 0.15f, 0f, 0.08f, 0.7f, 0f, 0f),        // below hearing, swelling
        SoundClass.Sounding => new(0.45f, 0.35f, 0.05f, 1.1f, 0.18f, 73f, 0.35f), // counting
        SoundClass.Geology => new(0.6f, 0.5f, 0.3f, 0f, 1f, 0f, 0f),           // cracks, slumps
        SoundClass.Whale => new(0.85f, 0.55f, 0.1f, 0.12f, 0.8f, 0f, 0f),      // song: long low glides
        _ => new(0.3f, 0.3f, 0.3f, 0f, 1f, 0f, 0f),
    };

    /// <summary>What the console says about a signature before it's identified.</summary>
    public static string Describe(SoundClass c)
    {
        var s = Of(c);
        string band = s.Low >= s.Mid && s.Low >= s.High ? "LOW" : s.High > s.Mid ? "HIGH" : "MID";
        string rhythm = s.PulseHz <= 0f ? "CONTINUOUS" : s.PulseHz < 0.5f ? $"SLOW SWELL {s.PulseHz:0.0#} Hz" : $"PULSED {s.PulseHz:0.0} Hz";
        string tone = s.Tone > 0f ? $" · TONE {s.ToneHz:0} Hz" : "";
        return $"{band} · {rhythm}{tone}";
    }

    public static string Name(SoundClass c) => c switch
    {
        SoundClass.Ravager => "RAVAGER",
        SoundClass.Wyrm => "FANGWYRM",
        SoundClass.Ray => "CRYSTAL RAYS",
        SoundClass.Vent => "THERMAL VENT",
        SoundClass.Station => "STATION MACHINERY",
        SoundClass.Listener => "BIOLOGIC, VERY LARGE",
        SoundClass.Sounding => "THE SOUNDING",
        SoundClass.Geology => "GEOLOGIC",
        SoundClass.Whale => "WHALE OF THE ABYSS",
        _ => "UNKNOWN",
    };

    public float BandGain(Signature s) => Filter switch
    {
        Band.Low => s.Low,
        Band.Mid => s.Mid,
        Band.High => s.High,
        _ => Math.Max(s.Low, Math.Max(s.Mid, s.High)),
    };

    /// <summary>The recording a class would match, if any.</summary>
    public Recording MatchOf(SoundClass c)
    {
        foreach (var r in Recordings) if (r.Class == c) return r;
        return null;
    }

    public static float Wrap(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; if (deg < -180f) deg += 360f; return deg; }

    /// <summary>How strongly a source at `bearing` lands in a beam pointing at `beam` (Gaussian).</summary>
    public static float BeamWeight(float beam, float bearing, float width) { float d = Wrap(bearing - beam) / width; return MathF.Exp(-d * d); }

    /// <param name="selfNoise">the boat's own noise 0..1 (from the Listener's measurement)</param>
    public void Update(float dt, IReadOnlyList<HydroSource> sources, float selfNoise)
    {
        _rowT -= dt;
        if (_rowT > 0f) return;
        _rowT = RowInterval;
        // Scroll down one row.
        for (int r = Rows - 1; r > 0; r--)
            for (int b = 0; b < Bins; b++) Waterfall[r, b] = Waterfall[r - 1, b];
        for (int b = 0; b < Bins; b++)
        {
            float bearing = -180f + (b + 0.5f) * BinDeg;
            float aft = MathF.Abs(bearing) / 180f;                        // 0 at the bow, 1 astern (the screw)
            float v = 0.04f + (float)_rng.NextDouble() * 0.06f;           // the sea itself
            v += selfNoise * (0.6f + 0.9f * aft * aft) * (0.7f + 0.3f * (float)_rng.NextDouble());
            foreach (var s in sources)
                v += s.Level * BandGain(Of(s.Class)) * BeamWeight(bearing, s.Bearing, s.Spread) * (0.8f + 0.2f * (float)_rng.NextDouble());
            Waterfall[0, b] = MathF.Min(1.2f, v);
        }
        Version++;
    }
}
