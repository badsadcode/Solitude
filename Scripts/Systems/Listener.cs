using System;

namespace Solitude.Systems;

/// <summary>
/// THE LISTENER. Something down here hunts by sound.
///
/// Every frame the game reports how loud the boat (or the diver) is: <see cref="Noise"/> 0..1, built
/// from the engine, flow noise, the floods, the intake, alarms, and short loud transients
/// (<see cref="Pulse"/>: sonar pings, impacts, harpoon shots). Noise above a quiet threshold feeds
/// <see cref="Attention"/> (0..100), faster the deeper you are; silence lets it drain away.
///
/// Attention has four tiers, each with its own events (Main drives the actual sounds and creatures):
///   Calm (&lt;30)      nothing.
///   Stirring (30+)   distant calls, more knocks; a ping may be ANSWERED (an echo from nothing).
///   Hunting (55+)    the ravager and the wyrm come looking.
///   Found (85+)      an encounter: something enormous passes beneath. Go quiet before it arrives
///                    and it passes; stay loud and it hits the boat.
/// Pure logic, no Godot nodes, so it's easy to test and to save (only Attention is persisted).
/// </summary>
public sealed class Listener
{
    public enum Tier { Calm, Stirring, Hunting, Found }

    public float Attention;           // 0..100
    public float Noise { get; private set; }  // smoothed, 0..1
    public float Pulse { get; private set; }  // decaying transient part of the noise
    public Tier Current { get; private set; }

    /// <summary>The noise level the Listener can't pick out from the ocean.</summary>
    public const float Quiet = 0.14f;

    /// <summary>Encounter state (Found): seconds until it arrives, and how quiet you've been.</summary>
    public float EncounterT { get; private set; } = -1f;
    public bool InEncounter => EncounterT >= 0f;
    public const float EncounterLength = 22f;
    private float _quietFor, _cooldown;

    public event Action<Tier, Tier> TierChanged;
    public event Action EncounterStarted;
    /// <summary>true = you went quiet and it passed; false = it found you.</summary>
    public event Action<bool> EncounterEnded;

    /// <summary>A loud moment (ping, impact, shot). Adds to the noise and decays over a few seconds.</summary>
    public void AddPulse(float amount) => Pulse = Math.Min(1.5f, Pulse + amount);

    /// <summary>A sonar ping is a shout: it also lands straight on its attention.</summary>
    public void HeardPing() { AddPulse(0.7f); Attention = Math.Min(100f, Attention + 4f * _depthF); }
    private float _depthF = 0.5f;

    /// <param name="steadyNoise">continuous noise this frame (engine, lights...), 0..1</param>
    /// <param name="depth">metres; the Listener lives deep</param>
    /// <param name="shielded">docked / inside a station: the station's mass hides you, no events</param>
    public void Update(float dt, float steadyNoise, float depth, bool shielded, float floor)
    {
        Pulse *= MathF.Exp(-1.2f * dt);
        float raw = steadyNoise + Pulse;
        if (shielded) raw *= 0.3f;
        Noise += (raw - Noise) * Math.Clamp(3f * dt, 0f, 1f);

        float depthF = Math.Clamp((depth - 150f) / 700f, 0.15f, 1.6f);
        // Tuned so AHEAD FULL at ~470 m draws it in about six minutes, at 1000 m in about two;
        // AHEAD SLOW with the floods off sits under the threshold (silent running).
        if (Noise > Quiet) Attention += (Noise - Quiet) * 1.1f * depthF * dt;
        else Attention -= ((Quiet - Noise) * 4f + 0.25f) * dt;
        _depthF = depthF;
        // Story progress keeps it from ever quite forgetting you.
        Attention = Math.Clamp(Attention, floor, 100f);

        _cooldown -= dt;
        if (InEncounter)
        {
            EncounterT += dt;
            _quietFor = Noise < Quiet ? _quietFor + dt : 0f;
            if (EncounterT >= EncounterLength)
            {
                bool passed = _quietFor >= 6f;
                EncounterT = -1f;
                _cooldown = 240f;
                Attention = passed ? 45f : 55f;
                EncounterEnded?.Invoke(passed);
            }
        }
        else if (!shielded && Attention >= 85f && _cooldown <= 0f)
        {
            EncounterT = 0f;
            _quietFor = 0f;
            EncounterStarted?.Invoke();
        }

        var t = InEncounter || Attention >= 85f ? Tier.Found : Attention >= 55f ? Tier.Hunting : Attention >= 30f ? Tier.Stirring : Tier.Calm;
        // A little hysteresis so it doesn't flicker at a boundary.
        if (t < Current && Attention > Threshold(Current) - 6f) t = Current;
        if (t != Current) { var old = Current; Current = t; TierChanged?.Invoke(old, t); }
    }

    private static float Threshold(Tier t) => t switch { Tier.Stirring => 30f, Tier.Hunting => 55f, Tier.Found => 85f, _ => 0f };

    /// <summary>Dev / capture: jump straight into an encounter.</summary>
    public void ForceEncounter() { Attention = 90f; _cooldown = 0f; }
}
