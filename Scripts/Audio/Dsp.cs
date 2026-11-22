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
