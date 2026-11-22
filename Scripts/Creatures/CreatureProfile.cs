using System.Collections.Generic;

namespace Solitude.Creatures;

/// <summary>How a creature behaves toward the boat and divers.</summary>
public enum Temperament
{
    /// <summary>Wanders near where it spawned; curious about lights; harmless.</summary>
    Grazer,
    /// <summary>Circles far out, makes long passes; may brush the hull or take a diver.</summary>
    Apex,
}

/// <summary>
/// Everything Swimmer needs to bring one of Tom's rigged-but-unanimated models to life.
/// Bones are named as in the Blender export (Bone, Bone.001 ...). The model's front is +Z.
///
/// Motion channels (all angles in radians, phases in radians of the swim cycle):
///   Yaw   — side-to-side wave for spines and tails (rotation about the model's up axis)
///   Pitch — up/down (rotation about the model's right axis): jaws, ray tails
///   Roll  — rotation about the model's forward axis: wing beats (sign = left/right)
/// A bone's motion = Amp * sin(cycle - Phase) + Curl * turn rate. Amp grows toward the tip
/// for a natural travelling wave.
/// </summary>
public sealed class CreatureProfile
{
    public string Name;
    public string Scene;                 // res:// path of the GLB
    public float Length;                 // desired length in metres (model is rescaled)
    public float ModelLength;            // the model's own +Z extent (bounds max-min)
    public Temperament Temperament;
    public float CruiseSpeed, PassSpeed;
    public float TurnRate = 0.4f;        // rad/s
    public float CycleBase = 0.3f, CyclePerSpeed = 0.08f; // swim beats per second
    public float EmissionEnergy = 3f;

    public readonly List<(string bone, float amp, float phase, float curl)> Yaw = new();
    public readonly List<(string bone, float amp, float phase)> Pitch = new();
    public readonly List<(string bone, float amp, float phase)> Roll = new();
    public string Jaw;                   // opens toward targets when close
    public float JawOpen = 0.5f;

    // Apex
    public float OrbitRadius = 120f, PassDistance = 30f, PassIntervalMin = 70f, PassIntervalMax = 140f;
    public float HullBrushChance = 0.2f, HullDamage = 8f, DiverBite = 70f, DiverBiteAfter = 90f;
    // Grazer
    public float WanderRadius = 30f, CuriosityRange = 45f, CuriousOrbit = 14f;

    public static CreatureProfile Fangwyrm()
    {
        // 40 m sea serpent. Spine: root → neck → skull (+ jaw); tail: five bones to the fluke.
        var p = new CreatureProfile
        {
            Name = "Abyssal Fangwyrm", Scene = "res://assets/Abyssal_Fangwyrm.glb",
            Length = 40f, ModelLength = 39.8f, Temperament = Temperament.Apex,
            CruiseSpeed = 4.5f, PassSpeed = 8f, TurnRate = 0.22f, CycleBase = 0.18f, CyclePerSpeed = 0.045f,
            EmissionEnergy = 4f, Jaw = "Bone.008", JawOpen = 0.45f,
            OrbitRadius = 140f, PassDistance = 32f, PassIntervalMin = 60f, PassIntervalMax = 120f,
        };
        p.Yaw.Add(("Bone.001", -0.05f, 1.8f, 0.2f));
        p.Yaw.Add(("Bone.007", -0.06f, 2.4f, 0.15f));
        p.Yaw.Add(("Bone", 0.04f, 0.9f, 0.1f));
        p.Yaw.Add(("Bone.002", 0.08f, 0f, 0.15f));
        p.Yaw.Add(("Bone.003", 0.12f, 0.8f, 0.2f));
        p.Yaw.Add(("Bone.004", 0.16f, 1.6f, 0.25f));
        p.Yaw.Add(("Bone.005", 0.2f, 2.4f, 0.3f));
        p.Yaw.Add(("Bone.006", 0.26f, 3.2f, 0.35f));
        return p;
    }

    public static CreatureProfile CrystalRay()
    {
        // 9 m wingspan ray. Disc/head = Bone; tail whip Bone.001 → .004; wings .005→.008 (right), .006→.007 (left).
        var p = new CreatureProfile
        {
            Name = "Abyssal Crystal Ray", Scene = "res://assets/Abyssal_Crystal_Ray.glb",
            Length = 10f, ModelLength = 9.9f, Temperament = Temperament.Grazer,
            CruiseSpeed = 1.8f, PassSpeed = 2.6f, TurnRate = 0.35f, CycleBase = 0.22f, CyclePerSpeed = 0.06f,
            EmissionEnergy = 2.6f,
            WanderRadius = 35f, CuriosityRange = 50f, CuriousOrbit = 13f,
        };
        // Wing beats ripple from root to tip; the right wing is +roll, the left -roll (both rise together).
        p.Roll.Add(("Bone.005", 0.32f, 0f));
        p.Roll.Add(("Bone.008", 0.42f, 0.7f));
        p.Roll.Add(("Bone.006", -0.32f, 0f));
        p.Roll.Add(("Bone.007", -0.42f, 0.7f));
        // Whip tail trails with a lazy wave.
        p.Yaw.Add(("Bone.001", 0.05f, 0.5f, 0.1f));
        p.Yaw.Add(("Bone.002", 0.1f, 1.2f, 0.15f));
        p.Yaw.Add(("Bone.003", 0.16f, 1.9f, 0.2f));
        p.Yaw.Add(("Bone.004", 0.22f, 2.6f, 0.25f));
        p.Pitch.Add(("Bone", 0.03f, 1.0f));
        return p;
    }
}
