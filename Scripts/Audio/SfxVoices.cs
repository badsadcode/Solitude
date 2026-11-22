using System;

namespace Solitude.Audio;

/// <summary>
/// One-shot effects for stations and combat, each a tiny synthesis recipe:
///   pistol / rifle / shotgun — noise crack + chest thump + metallic ring (the bus reverb does the rest)
///   chitter — stuttering clicks through a narrow band (many small legs on metal)
///   screech — FM shriek sweeping up, with breath noise
///   hiss / hurt / die — breathy noise bursts with a falling formant
///   beacon — two clean distant pips; airlock — long filtered hiss with a pressure tone
///   door — servo grind; deny — two low buzzes; pickup — short rising chirp; empty / reload — clicks
/// </summary>
public sealed class SfxVoice : Voice
{
    private readonly string _kind;
    private readonly float _dur;
    private readonly BandPass _bp1, _bp2;
    private OnePole _lp;
    private float _ph1, _ph2, _clk;

    public SfxVoice(float rate, Random rng, string kind, float pan, float gain) : base(rate, rng)
    {
        _kind = kind;
        Pan = Math.Clamp(pan, -1f, 1f);
        float jitter = 0.9f + (float)rng.NextDouble() * 0.2f;
        (_dur, Gain) = kind switch
        {
            "pistol" => (0.9f, 0.3f),
            "rifle" => (0.6f, 0.24f),
            "shotgun" => (1.4f, 0.38f),
            "chitter" => (0.7f, 0.35f),
            "screech" => (0.9f, 0.6f),
            "hiss" => (0.5f, 0.5f),
            "hurt" => (0.35f, 0.55f),
            "die" => (1.4f, 0.6f),
            "beacon" => (0.9f, 0.22f),
            "airlock" => (3.0f, 0.5f),
            "door" => (2.4f, 0.4f),
            "deny" => (0.5f, 0.3f),
            "pickup" => (0.18f, 0.3f),
            "empty" => (0.08f, 0.35f),
            "reload" => (0.5f, 0.3f),
            _ => (0.2f, 0.2f),
        };
        Gain *= gain;
        _bp1 = new BandPass(kind switch { "chitter" => 2600f * jitter, "shotgun" => 900f, "rifle" => 1500f, "pistol" => 1800f, "door" => 300f, _ => 1200f * jitter }, kind == "chitter" ? 12f : 2.5f, rate);
        _bp2 = new BandPass(kind switch { "pistol" => 2900f, "rifle" => 3400f, "shotgun" => 2100f, _ => 700f }, 30f, rate);
        _lp = new OnePole(kind switch { "airlock" => 1800f, "beacon" => 2500f, "die" => 1400f, _ => 4000f }, rate);
    }

    public override float Next(out bool alive)
    {
        float dt = 1f / Rate;
        T += dt;
        alive = T < _dur;
        float u = T / _dur;
        float w = White();
        float s = 0f;
        switch (_kind)
        {
            case "pistol":
            case "rifle":
            case "shotgun":
            {
                float decay = _kind == "shotgun" ? 9f : _kind == "rifle" ? 22f : 16f;
                float crack = _bp1.Process(w) * MathF.Exp(-T * decay) * 3.5f;
                _ph1 += (_kind == "shotgun" ? 48f : 62f) * dt;
                float thump = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * (_kind == "shotgun" ? 7f : 12f)) * 1.4f;
                float ring = _bp2.Process(w * MathF.Exp(-T * 60f)) * 6f * MathF.Exp(-T * 3f);
                s = crack + thump + ring + w * MathF.Exp(-T * 120f) * 1.5f;
                break;
            }
            case "chitter":
            {
                _clk += (26f + 14f * MathF.Sin(T * 9f)) * dt;
                float imp = 0f;
                if (_clk >= 1f) { _clk -= 1f; imp = 0.5f + (float)Rng.NextDouble(); }
                s = _bp1.Process(imp + w * 0.02f) * 3f * MathF.Sin(MathF.PI * u);
                break;
            }
            case "screech":
            {
                _ph2 += 70f * dt;
                float f = 700f + 900f * u + 200f * MathF.Sin(T * 31f);
                _ph1 += f * (1f + 0.8f * MathF.Sin(_ph2 * MathF.Tau)) * dt;
                s = (MathF.Sin(_ph1 * MathF.Tau) * 0.7f + _bp1.Process(w) * 1.5f) * MathF.Pow(MathF.Max(0f, MathF.Sin(MathF.PI * u)), 0.6f);
                break;
            }
            case "hiss":
            case "hurt":
            case "die":
            {
                float formant = _kind == "die" ? 900f * (1f - 0.7f * u) : 1400f - 600f * u;
                _bp1.Set(formant, 3f, Rate);
                _ph1 += (_kind == "die" ? 220f * (1f - 0.6f * u) : 380f) * dt;
                float voiced = _kind == "hiss" ? 0f : MathF.Sin(_ph1 * MathF.Tau) * 0.4f * (1f - u);
                s = (_bp1.Process(w) * 2.2f + voiced) * MathF.Max(0f, MathF.Sin(MathF.PI * MathF.Min(1f, u * 1.3f)));
                break;
            }
            case "beacon":
            {
                float f = T < 0.35f ? 1760f : 1480f;
                _ph1 += f * dt;
                float env = (T < 0.25f ? MathF.Min(1f, T * 200f) * MathF.Exp(-T * 10f) : 0f)
                          + (T > 0.35f && T < 0.65f ? MathF.Min(1f, (T - 0.35f) * 200f) * MathF.Exp(-(T - 0.35f) * 10f) : 0f);
                s = _lp.Process(MathF.Sin(_ph1 * MathF.Tau)) * env;
                break;
            }
            case "airlock":
            {
                float env = MathF.Min(1f, T * 3f) * MathF.Max(0f, 1f - u * u);
                _ph1 += (90f - 40f * u) * dt;
                s = _lp.Process(w) * 1.4f * env + MathF.Sin(_ph1 * MathF.Tau) * 0.25f * env;
                break;
            }
            case "door":
            {
                _ph1 += (55f + 10f * MathF.Sin(T * 13f)) * dt;
                float saw = _ph1 - MathF.Floor(_ph1);
                s = _bp1.Process(saw * 2f - 1f + w * 0.3f) * 2f * MathF.Min(1f, T * 6f) * MathF.Min(1f, (_dur - T) * 4f);
                break;
            }
            case "deny":
            {
                _ph1 += 110f * dt;
                float sq = (_ph1 - MathF.Floor(_ph1)) < 0.5f ? 1f : -1f;
                s = sq * 0.3f * ((T % 0.25f) < 0.17f ? 1f : 0f);
                break;
            }
            case "pickup":
            {
                _ph1 += (900f + 1400f * u) * dt;
                s = MathF.Sin(_ph1 * MathF.Tau) * MathF.Sin(MathF.PI * u) * 0.6f;
                break;
            }
            case "empty":
                s = _bp1.Process(w) * MathF.Exp(-T * 200f) * 3f;
                break;
            case "reload":
                s = _bp1.Process(w) * (MathF.Exp(-T * 120f) + MathF.Exp(-MathF.Max(0f, T - 0.3f) * 120f) * (T > 0.3f ? 1f : 0f)) * 3f;
                break;
        }
        return s * Gain;
    }
}
