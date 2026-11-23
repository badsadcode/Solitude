using System;

namespace Solitude.Audio;

/// <summary>RBJ biquad, band-pass (constant peak gain). Tiny and allocation-free.</summary>
public sealed class BandPass
{
    private float _b0, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

    public BandPass(float freq, float q, float rate) => Set(freq, q, rate);

    public void Set(float freq, float q, float rate)
    {
        float w = 2f * MathF.PI * Math.Clamp(freq, 10f, rate * 0.45f) / rate;
        float alpha = MathF.Sin(w) / (2f * q);
        float a0 = 1f + alpha;
        _b0 = alpha / a0;
        _b2 = -alpha / a0;
        _a1 = -2f * MathF.Cos(w) / a0;
        _a2 = (1f - alpha) / a0;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }
}

/// <summary>One-pole low-pass.</summary>
public struct OnePole
{
    private float _a, _y;
    public OnePole(float cutoff, float rate) { _a = 0; _y = 0; Set(cutoff, rate); }
    public void Set(float cutoff, float rate) => _a = 1f - MathF.Exp(-2f * MathF.PI * cutoff / rate);
    public float Process(float x) => _y += _a * (x - _y);
}

/// <summary>A short-lived synthesized sound. Return false from Render when finished.</summary>
public abstract class Voice
{
    public float Pan;   // -1 left .. 1 right
    public float Gain = 1f;
    protected float T;
    protected readonly float Rate;
    protected readonly Random Rng;

    /// <summary>Constructed on the main thread, rendered on the audio thread, so each voice owns its RNG.</summary>
    protected Voice(float rate, Random rng) { Rate = rate; Rng = new Random(rng.Next()); }

    protected float White() => (float)Rng.NextDouble() * 2f - 1f;

    /// <summary>Produce one mono sample; set alive=false when done.</summary>
    public abstract float Next(out bool alive);
}

/// <summary>
/// Bubbles: the gurgle of air leaving a diving regulator ("bulgotanie wody", Tom).
///
/// One bubble is a damped sine at its resonance (Minnaert: f ≈ 3.26 / radius m, so a 1 cm bubble rings
/// at ~330 Hz, a 2 mm one at ~1.6 kHz) whose pitch climbs as it rises and pops (van den Doel's model:
/// f(t) = f0 (1 + σt), damping grows with frequency). The gurgle is many of them at once, sizes spread
/// on a log scale, big slow glugs first then a fizz of small ones, streaming past one side of the helmet
/// or the other. Up to 48 bubbles ring at the same time; scheduled bubbles wait in a short queue.
/// </summary>
public sealed class BubbleBank
{
    private const int Max = 48;
    /// <summary>Tom: "one or two tones lower". 0.8 ≈ four semitones (two whole tones) down.</summary>
    public const float Pitch = 0.8f;
    private readonly float _rate;
    private readonly float[] _f = new float[Max], _ph = new float[Max], _env = new float[Max], _decay = new float[Max],
        _rise = new float[Max], _gl = new float[Max], _gr = new float[Max], _wait = new float[Max];
    private readonly bool[] _on = new bool[Max];
    private readonly Random _rng = new(7);
    private readonly OnePole _bodyL, _bodyR;   // a little low-pass: they're heard through water and a helmet

    public BubbleBank(float rate) { _rate = rate; _bodyL = new OnePole(2600f, rate); _bodyR = new OnePole(2600f, rate); }

    private float R => (float)_rng.NextDouble();

    /// <summary>Schedule one bubble of radius r (metres) after `delay` seconds, panned -1..1.</summary>
    private void Add(float radius, float delay, float pan, float loud)
    {
        for (int i = 0; i < Max; i++)
        {
            if (_on[i]) continue;
            float f0 = 3.26f / radius * Pitch;
            float d = 0.043f * f0 + 0.0014f * MathF.Pow(f0, 1.5f);   // damping (1/s)
            _f[i] = f0; _ph[i] = R; _decay[i] = d;
            _rise[i] = d * (0.1f + 0.3f * R);                         // how fast it chirps up
            // Big ones are fuller. Level: x3.7 over the first try, which sat ~18 dB under the sea's rumble
            // in the game (the demos had no sea under them, so they sounded fine): Tom couldn't hear them.
            float a = loud * MathF.Sqrt(radius / 0.004f) * 0.22f;
            _gl[i] = a * MathF.Sqrt(0.5f * (1f - pan)); _gr[i] = a * MathF.Sqrt(0.5f * (1f + pan));
            _env[i] = 1f; _wait[i] = delay; _on[i] = true;
            return;
        }
    }

    private float LogRadius(float minMm, float maxMm) => MathF.Exp(MathF.Log(minMm) + R * (MathF.Log(maxMm) - MathF.Log(minMm))) / 1000f;

    /// <summary>An exhale: a gush that comes out in two or three surges, glugs first, then a fizz.</summary>
    public void Exhale(float exertion, float motion = 1f)
    {
        float side = R < 0.5f ? -1f : 1f;                 // the stream rolls up past one side of the visor
        int n = (int)((26 + exertion * 22f + _rng.Next(8)) * Math.Clamp(motion, 0f, 1f));
        for (int k = 0; k < n; k++)
        {
            float t = MathF.Pow(R, 1.4f) * (1.1f - exertion * 0.35f);
            float surge = MathF.Sin(t * 9f) * 0.5f + 0.5f;    // bubbles come in bursts, not a smooth hiss
            if (R > 0.35f + surge * 0.65f) t += 0.06f;
            bool glug = k < 6 || R < 0.18f;
            float radius = glug ? LogRadius(6f, 14f) : LogRadius(1.4f, 5.5f);
            float pan = Math.Clamp(side * (0.25f + 0.6f * R) + (R - 0.5f) * 0.3f, -1f, 1f);
            Add(radius, t, pan, glug ? 1.1f : 0.75f);
        }
    }

    /// <summary>A stray bubble or two between breaths.</summary>
    public void Stray()
    {
        int n = 1 + (R < 0.3f ? 2 : 0);
        for (int k = 0; k < n; k++) Add(LogRadius(2f, 7f), k * 0.05f, R * 2f - 1f, 0.45f);
    }

    /// <summary>Bubbles ringing or waiting right now (diagnostics).</summary>
    public int Count { get { int n = 0; for (int i = 0; i < Max; i++) if (_on[i]) n++; return n; } }

    public (float l, float r) Next(float dt)
    {
        float l = 0f, r = 0f;
        for (int i = 0; i < Max; i++)
        {
            if (!_on[i]) continue;
            if (_wait[i] > 0f) { _wait[i] -= dt; continue; }
            _f[i] *= 1f + _rise[i] * dt;
            _ph[i] += _f[i] * dt; _ph[i] -= MathF.Floor(_ph[i]);
            float v = MathF.Sin(_ph[i] * MathF.Tau) * _env[i];
            l += v * _gl[i]; r += v * _gr[i];
            _env[i] *= 1f - Math.Min(1f, _decay[i] * dt);
            if (_env[i] < 0.002f || _f[i] > 9000f) _on[i] = false;
        }
        return (_bodyL.Process(l), _bodyR.Process(r));
    }
}
