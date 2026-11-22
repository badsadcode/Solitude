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
    /// <summary>Surface: the wet sheen (0..1) and roughness. The swimmers' defaults are glossy; skins can be matte.</summary>
    public float WetSpec = 0.6f, Roughness = 0.45f;

    public readonly List<(string bone, float amp, float phase, float curl)> Yaw = new();
    public readonly List<(string bone, float amp, float phase)> Pitch = new();
    public readonly List<(string bone, float amp, float phase)> Roll = new();
    public float BodySway;               // whole-body heading sway with the swim cycle (rad), for serpents
    public string Jaw;                   // opens toward targets when close
    public float JawOpen = 0.5f;
    public float JawClosed = 0f;         // offset that shuts a jaw sculpted open in the rest pose

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
            EmissionEnergy = 4f, Jaw = "Bone.008", JawOpen = 0.3f, JawClosed = -0.78f,
            OrbitRadius = 140f, PassDistance = 32f, PassIntervalMin = 60f, PassIntervalMax = 120f,
        };
        // Serpentine (anguilliform) swimming, tuned after Tom: "too stiff, it's a serpent".
        // One travelling wave runs the whole body, skull → tail tip, ~1 wavelength along it.
        // The head chain points forward (+Z) and the tail chain backward, so the head bones take
        // the opposite sign to keep the S-curve continuous through the root.
        const float k = 1.0f; // phase step per joint (radians)
        p.Yaw.Add(("Bone.007", 0.07f, 0 * k, -0.1f));     // skull: small, keeps the eyes on target
        p.Yaw.Add(("Bone.001", -0.2f, 1 * k, -0.25f));    // neck
        p.Yaw.Add(("Bone", -0.18f, 2 * k, -0.2f));        // front body
        p.Yaw.Add(("Bone.002", 0.21f, 3 * k, 0.25f));     // tail chain
        p.Yaw.Add(("Bone.003", 0.25f, 4 * k, 0.3f));
        p.Yaw.Add(("Bone.004", 0.27f, 5 * k, 0.3f));
        p.Yaw.Add(("Bone.005", 0.3f, 6 * k, 0.35f));
        p.Yaw.Add(("Bone.006", 0.33f, 7 * k, 0.35f));
        // A slower vertical ripple down the tail: sea-dragon, not eel-on-a-table.
        p.Pitch.Add(("Bone.003", 0.05f, 4 * k + 1.6f));
        p.Pitch.Add(("Bone.004", 0.07f, 5 * k + 1.6f));
        p.Pitch.Add(("Bone.005", 0.08f, 6 * k + 1.6f));
        p.Pitch.Add(("Bone.006", 0.1f, 7 * k + 1.6f));
        p.BodySway = 0.1f;
        return p;
    }

    public static CreatureProfile Whale()
    {
        // Tom's "Whale of the Abyss", a gentle giant. Spine: Bone -> .004 -> .001 -> .002 -> .003 (fluke),
        // running from behind the head to the tail; Bone.005 the head; Bone.L/R + Bone2 the pectoral fins.
        // Whales swim with the tail going UP AND DOWN, so the travelling wave is pitch, not yaw.
        var p = new CreatureProfile
        {
            Name = "Whale of the Abyss", Scene = "res://assets/Whale_of_the_Abyss.glb",
            Length = 70f, ModelLength = 19.24f, Temperament = Temperament.Grazer,   // Tom: "grow it bigger"
            CruiseSpeed = 2.4f, PassSpeed = 2.8f, TurnRate = 0.05f, CycleBase = 0.07f, CyclePerSpeed = 0.012f,
            // Tom's glow map: dim teal patches over ~4% of the skin, painted gently, so it can run hot.
            EmissionEnergy = 2.6f,
            WetSpec = 0.1f, Roughness = 0.85f,   // Tom: "specular is too high"
        };
        const float k = 0.7f;
        p.Pitch.Add(("Bone", 0.015f, 0 * k));
        p.Pitch.Add(("Bone.004", 0.035f, 1 * k));
        p.Pitch.Add(("Bone.001", 0.06f, 2 * k));
        p.Pitch.Add(("Bone.002", 0.1f, 3 * k));
        p.Pitch.Add(("Bone.003", 0.16f, 4 * k));      // the fluke
        p.Pitch.Add(("Bone.005", 0.02f, -0.6f));     // the head nods against the stroke
        p.Roll.Add(("Bone.L", 0.08f, 0.5f));          // fins scull slowly
        p.Roll.Add(("Bone2.L", 0.1f, 1.1f));
        p.Roll.Add(("Bone.R", -0.08f, 0.5f));
        p.Roll.Add(("Bone2.R", -0.1f, 1.1f));
        p.Yaw.Add(("Bone.003", 0.02f, 4 * k, 0.15f)); // a little steering in the fluke on turns
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
