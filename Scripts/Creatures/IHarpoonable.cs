using Godot;

namespace Solitude.Creatures;

/// <summary>
/// A sea creature the diver's harpoon can hit. Bodies are long, so hits are tested against a
/// capsule along the spine (Head → Tail) with a radius.
/// </summary>
public interface IHarpoonable
{
    Vector3 Head { get; }
    Vector3 Tail { get; }
    float BodyRadius { get; }
    /// <summary>Take a hit. shock = the prod: little damage, but a stun that breaks off an attack.</summary>
    void Hurt(float damage, Vector3 from, bool shock);
    /// <summary>Somewhere on the body for a spear to stick to (local to the creature).</summary>
    Node3D Body { get; }
}

public static class Hit
{
    /// <summary>Distance from p to the segment a-b.</summary>
    public static float ToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-4f), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
