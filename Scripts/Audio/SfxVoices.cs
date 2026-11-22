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
            "klaxon" => (2.4f, 0.22f),
            "knock" => (0.9f, 0.9f),
            "drip" => (0.35f, 0.18f),
            "steam" => (1.6f, 0.32f),
            "weld" => (0.35f, 0.25f),
            "breach" => (1.8f, 0.7f),
            "harpoon" => (0.7f, 0.55f),
            "shock" => (0.55f, 0.45f),
            "reel" => (0.06f, 0.25f),
            "spearhit" => (0.4f, 0.6f),
            "listener" => (7.0f, 0.55f),
            "powerup" => (4.0f, 0.6f),
            "whalesong" => (9.0f, 0.5f),
            _ => (0.2f, 0.2f),
        };
        Gain *= gain;
        _bp1 = new BandPass(kind switch { "chitter" => 2600f * jitter, "shotgun" => 900f, "rifle" => 1500f, "pistol" => 1800f, "door" => 300f, "knock" => 220f * jitter, "weld" => 4200f, "shock" => 3600f, "reel" => 2400f, "spearhit" => 500f, _ => 1200f * jitter }, kind == "chitter" ? 12f : 2.5f, rate);
        _bp2 = new BandPass(kind switch { "pistol" => 2900f, "rifle" => 3400f, "shotgun" => 2100f, "knock" => 160f * jitter, _ => 700f }, 30f, rate);
        _lp = new OnePole(kind switch { "airlock" => 1800f, "beacon" => 2500f, "die" => 1400f, "klaxon" => 1800f, "breach" => 600f, "harpoon" => 900f, "listener" => 260f, "whalesong" => 900f, _ => 4000f }, rate);
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
            case "klaxon":
            {
                // Two-tone alarm, slightly detuned square, softened.
                float f = (T % 0.8f) < 0.4f ? 440f : 554f;
                _ph1 += f * dt; _ph1 -= MathF.Floor(_ph1);
                float sq = _ph1 < 0.5f ? 1f : -1f;
                s = _lp.Process(sq) * 0.5f * MathF.Min(1f, (_dur - T) * 4f);
                break;
            }
            case "knock":
            {
                // Something solid against the outside of a steel hull: thud + the hull ringing.
                _ph1 += 52f * dt;
                float thud = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * 9f) * 1.6f;
                float hit = _bp1.Process(w * MathF.Exp(-T * 90f) * 4f);
                float ring = _bp2.Process(w * MathF.Exp(-T * 60f)) * 5f * MathF.Exp(-T * 4f);
                s = thud + hit * 0.5f + ring * 0.6f;
                break;
            }
            case "drip":
            {
                // A drop: a quick upward chirp, then a tiny ring.
                float f = 1200f + 1400f * MathF.Min(1f, T * 30f);
                _ph1 += f * dt; _ph1 -= MathF.Floor(_ph1);
                s = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * 28f);
                break;
            }
            case "steam":
            {
                // Pressurised hiss: bright noise, quick attack, long tail.
                _bp1.Set(3200f, 0.8f, Rate);
                float env = MathF.Min(1f, T * 20f) * MathF.Exp(-T * 2.2f);
                s = _bp1.Process(w) * 2.5f * env;
                break;
            }
            case "weld":
            {
                // Arc crackle: noise gated by random bursts.
                if (Rng.NextDouble() < 0.02) _clk = 1f;
                _clk *= 0.995f;
                s = _bp1.Process(w) * 2f * _clk * MathF.Min(1f, (_dur - T) * 8f);
                break;
            }
            case "breach":
            {
                // The hull giving way: a deep crack, then water forcing its way in.
                _ph1 += (70f - 30f * u) * dt;
                float crack = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * 3f) * 1.4f + w * MathF.Exp(-T * 30f);
                float rush = _lp.Process(w) * MathF.Min(1f, T * 3f) * 0.8f;
                s = crack + rush;
                break;
            }
            case "harpoon":
            {
                // Gas-driven spear: a muffled pneumatic thump, a bubbling whoosh, line paying out.
                _ph1 += (85f - 40f * u) * dt;
                float thump = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * 14f) * 1.6f;
                float whoosh = _lp.Process(w) * MathF.Exp(-T * 5f) * 1.2f;
                _clk += (60f - 50f * u) * dt;
                float tick = 0f;
                if (_clk >= 1f) { _clk -= 1f; tick = 0.6f; }
                s = thump + whoosh + _bp2.Process(tick) * 2f * MathF.Exp(-T * 3f);
                break;
            }
            case "shock":
            {
                // Electric discharge in water: buzz at mains-ish frequency, ripped through with crackle.
                _ph1 += 95f * dt; _ph1 -= MathF.Floor(_ph1);
                float buzz = (_ph1 < 0.5f ? 1f : -1f) * 0.35f;
                if (Rng.NextDouble() < 0.04) _clk = 1f;
                _clk *= 0.993f;
                float crackle = _bp1.Process(w) * 2.5f * _clk;
                s = (buzz + crackle) * MathF.Min(1f, T * 80f) * MathF.Exp(-T * 4f);
                break;
            }
            case "whalesong":
            {
                // A long call: a voice that slides up, wavers and falls away, with a second voice a
                // fifth above it fading in and out, the whole thing smeared by water.
                float env = MathF.Pow(MathF.Sin(MathF.PI * u), 1.5f);
                float glide = 70f + 120f * MathF.Sin(MathF.PI * MathF.Min(1f, u * 1.3f)) - 40f * u;
                float vib = 1f + 0.015f * MathF.Sin(T * 5.3f);
                _ph1 += glide * vib * dt; _ph1 -= MathF.Floor(_ph1);
                _ph2 += glide * 1.5f * vib * dt; _ph2 -= MathF.Floor(_ph2);
                float voice = MathF.Sin(_ph1 * MathF.Tau) + 0.35f * MathF.Sin(_ph1 * 2f * MathF.Tau) + 0.15f * MathF.Sin(_ph1 * 3f * MathF.Tau);
                float second = MathF.Sin(_ph2 * MathF.Tau) * 0.4f * MathF.Max(0f, MathF.Sin(T * 0.9f));
                s = _lp.Process((voice + second) * 0.45f + w * 0.02f) * env;
                break;
            }
            case "powerup":
            {
                // A heavy relay clunk, then a machine spinning up: a hum rising from nothing to mains,
                // with the high whine of something large getting up to speed.
                float clunk = MathF.Sin(T * 60f * MathF.Tau) * MathF.Exp(-T * 18f) * 1.5f + _bp1.Process(w) * MathF.Exp(-T * 40f) * 2f;
                float spin = MathF.Min(1f, MathF.Max(0f, T - 0.3f) / 2.5f);
                _ph1 += (18f + 32f * spin) * dt; _ph1 -= MathF.Floor(_ph1);
                _ph2 += (300f + 1500f * spin) * dt; _ph2 -= MathF.Floor(_ph2);
                float hum = (MathF.Sin(_ph1 * MathF.Tau) + 0.4f * MathF.Sin(_ph1 * 2f * MathF.Tau)) * 0.5f * spin;
                float whine = MathF.Sin(_ph2 * MathF.Tau) * 0.06f * spin;
                float tail = MathF.Min(1f, (_dur - T) * 1.5f);
                s = (clunk + hum + whine) * tail;
                break;
            }
            case "listener":
            {
                // Something vast making a sound below hearing: two slow, beating low tones that swell
                // and sag in pitch, with a grinding texture riding on top.
                float env = MathF.Sin(MathF.PI * u) * MathF.Min(1f, T * 1.5f);
                float sag = 1f - 0.25f * u;
                _ph1 += 52f * sag * dt; _ph1 -= MathF.Floor(_ph1);
                _ph2 += 55.5f * sag * dt; _ph2 -= MathF.Floor(_ph2);
                float tones = MathF.Sin(_ph1 * MathF.Tau) * 0.6f + MathF.Sin(_ph2 * MathF.Tau) * 0.5f
                            + MathF.Sin(_ph1 * 2f * MathF.Tau) * 0.2f;
                float grind = _lp.Process(w) * (0.5f + 0.5f * MathF.Sin(T * 3.1f));
                s = (tones * 0.8f + grind * 1.2f) * env;
                break;
            }
            case "reel":
                // One ratchet click of the reel.
                s = _bp1.Process(w) * MathF.Exp(-T * 150f) * 3f;
                break;
            case "spearhit":
            {
                // Barbed steel into something: a dull thock, a short wet tear.
                _ph1 += (140f - 60f * u) * dt;
                s = MathF.Sin(_ph1 * MathF.Tau) * MathF.Exp(-T * 18f) * 1.4f + _bp1.Process(w) * MathF.Exp(-T * 12f) * 1.5f;
                break;
            }
        }
        return s * Gain;
    }
}
