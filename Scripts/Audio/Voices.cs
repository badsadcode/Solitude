using System;

namespace Solitude.Audio;

/// <summary>Hull creak: stick-slip friction pulses exciting two metal resonances.</summary>
public sealed class CreakVoice : Voice
{
    private readonly float _dur, _rate0, _rate1;
    private readonly BandPass _r1, _r2;
    private float _phase;

    public CreakVoice(float rate, Random rng, float intensity) : base(rate, rng)
    {
        _dur = 0.6f + (float)rng.NextDouble() * 2.2f;
        _rate0 = 12f + (float)rng.NextDouble() * 25f;
        _rate1 = _rate0 * (0.6f + (float)rng.NextDouble() * 1.4f);
        float f = 220f + (float)rng.NextDouble() * 520f;
        _r1 = new BandPass(f, 28f, rate);
        _r2 = new BandPass(f * (2.1f + (float)rng.NextDouble() * 0.6f), 22f, rate);
        Gain = 0.5f + intensity * 0.6f;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < _dur;
        float u = T / _dur;
        float env = MathF.Sin(MathF.PI * u);
        env *= env;
        float pr = _rate0 + (_rate1 - _rate0) * u;
        _phase += pr / Rate;
        float imp = 0f;
        if (_phase >= 1f)
        {
            _phase -= 1f;
            imp = (0.6f + (float)Rng.NextDouble() * 0.8f);
        }
        float x = imp + White() * 0.02f;
        return (_r1.Process(x) * 1.6f + _r2.Process(x) * 0.8f) * env * Gain;
    }
}

/// <summary>Low structural groan: a bent sawtooth through a resonant band.</summary>
public sealed class GroanVoice : Voice
{
    private readonly float _dur, _f0, _bend;
    private readonly BandPass _bp;
    private OnePole _lp;
    private float _ph;

    public GroanVoice(float rate, Random rng, float intensity) : base(rate, rng)
    {
        _dur = 2f + (float)rng.NextDouble() * 3f;
        _f0 = 32f + (float)rng.NextDouble() * 40f;
        _bend = (float)(rng.NextDouble() - 0.5) * 0.3f;
        _bp = new BandPass(140f + (float)rng.NextDouble() * 160f, 4f, rate);
        _lp = new OnePole(500f, rate);
        Gain = 0.35f + intensity * 0.5f;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < _dur;
        float u = T / _dur;
        float env = MathF.Pow(MathF.Sin(MathF.PI * u), 1.5f);
        float f = _f0 * (1f + _bend * u + 0.015f * MathF.Sin(T * 5.3f));
        _ph += f / Rate;
        _ph -= MathF.Floor(_ph);
        float saw = _ph * 2f - 1f;
        float x = saw + White() * 0.15f;
        return _lp.Process(_bp.Process(x) * 2.5f) * env * Gain;
    }
}

/// <summary>
/// Something very large, very far away. Slow downward glide with inharmonic partials,
/// heavily low-passed. Played rarely; never explained.
/// </summary>
public sealed class CallVoice : Voice
{
    private readonly float _dur, _f0, _glide;
    private static readonly float[] Mul = { 1f, 2.03f, 3.11f };
    private static readonly float[] Amp = { 1f, 0.45f, 0.25f };
    private readonly float[] _ph = new float[3];
    private OnePole _lp;

    public CallVoice(float rate, Random rng) : base(rate, rng)
    {
        _dur = 5f + (float)rng.NextDouble() * 5f;
        bool high = rng.NextDouble() < 0.3;
        _f0 = high ? 160f + (float)rng.NextDouble() * 120f : 48f + (float)rng.NextDouble() * 40f;
        _glide = 0.35f + (float)rng.NextDouble() * 0.3f;
        _lp = new OnePole(high ? 700f : 300f, rate);
        Gain = 0.55f;
        Pan = (float)(rng.NextDouble() * 2 - 1) * 0.8f;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < _dur;
        float u = T / _dur;
        float env = MathF.Min(1f, u * 4f) * MathF.Pow(1f - u, 1.6f);
        float f = _f0 * (1f - _glide * u * u) * (1f + 0.02f * MathF.Sin(T * 4.1f + MathF.Sin(T * 0.7f) * 3f));
        float s = 0f;
        for (int i = 0; i < 3; i++)
        {
            _ph[i] += f * Mul[i] / Rate;
            _ph[i] -= MathF.Floor(_ph[i]);
            float tri = 1f - 4f * MathF.Abs(_ph[i] - 0.5f);
            s += tri * Amp[i];
        }
        return _lp.Process(s) * env * Gain;
    }
}

/// <summary>Hull impact: low thud + metallic ring + scrape noise.</summary>
public sealed class ImpactVoice : Voice
{
    private readonly float _strength;
    private readonly BandPass _ring, _ring2;
    private OnePole _lp;
    private float _ph;

    public ImpactVoice(float rate, Random rng, float strength) : base(rate, rng)
    {
        _strength = Math.Clamp(strength, 0.2f, 3f);
        _ring = new BandPass(150f + (float)rng.NextDouble() * 60f, 45f, rate);
        _ring2 = new BandPass(410f + (float)rng.NextDouble() * 100f, 35f, rate);
        _lp = new OnePole(900f, rate);
        Gain = 0.45f + _strength * 0.35f;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < 2.5f;
        _ph += 42f / Rate;
        float thud = MathF.Sin(_ph * MathF.Tau) * MathF.Exp(-T * 4f);
        float burst = White() * MathF.Exp(-T * 18f);
        float scrape = _lp.Process(White()) * MathF.Exp(-T * 2.5f) * 0.6f;
        float ring = _ring.Process(burst * 3f) * 6f + _ring2.Process(burst * 2f) * 3f;
        return (thud * 1.4f + ring + scrape) * Gain;
    }
}

/// <summary>Boot on steel grating.</summary>
public sealed class StepVoice : Voice
{
    private readonly BandPass _bp, _rattle;

    public StepVoice(float rate, Random rng) : base(rate, rng)
    {
        _bp = new BandPass(260f + (float)rng.NextDouble() * 120f, 3f, rate);
        _rattle = new BandPass(1100f + (float)rng.NextDouble() * 400f, 25f, rate);
        Gain = 0.4f + (float)rng.NextDouble() * 0.15f;
        Pan = (float)(rng.NextDouble() - 0.5) * 0.2f;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < 0.35f;
        float n = White() * MathF.Exp(-T * 60f);
        return (_bp.Process(n) * 3f + _rattle.Process(n) * 1.2f) * Gain;
    }
}

/// <summary>Console blip / relay click.</summary>
public sealed class BlipVoice : Voice
{
    private readonly float _f, _dur;
    private float _ph;

    public BlipVoice(float rate, Random rng, float freq, float dur, float gain) : base(rate, rng)
    {
        _f = freq; _dur = dur; Gain = gain;
    }

    public override float Next(out bool alive)
    {
        T += 1f / Rate;
        alive = T < _dur;
        _ph += _f / Rate;
        _ph -= MathF.Floor(_ph);
        float sq = _ph < 0.5f ? 1f : -1f;
        float env = MathF.Min(1f, (_dur - T) * 60f) * MathF.Min(1f, T * 400f);
        return sq * env * Gain * 0.25f;
    }
}
