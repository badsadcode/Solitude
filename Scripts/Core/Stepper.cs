using Godot;

namespace Solitude.Core;

/// <summary>
/// Stair stepping for CharacterBody3D (players and creatures). Godot's MoveAndSlide treats a
/// step as a wall; this lifts the body onto it when there's headroom and ground beyond.
/// Call right before MoveAndSlide with the velocity you're about to apply.
/// </summary>
public static class Stepper
{
    /// <summary>Debug: why the last TryStepUp didn't step (printed by the stairs test).</summary>
    public static string Why = "";

    /// <summary>
    /// How far ahead to look for the tread: a bit more than the player's capsule radius. Probing only
    /// one frame ahead lands the capsule's rounded bottom on the step's front EDGE, which reads as
    /// steep ground — that made NoorLevel's 0.5 m station stairs unclimbable.
    /// </summary>
    public const float ProbeAhead = 0.34f;

    /// <returns>How far the body was lifted (0 if no step).</returns>
    public static float TryStepUp(CharacterBody3D body, float dt, float stepHeight)
    {
        if (!body.IsOnFloor()) return 0f;
        Vector3 motion = new Vector3(body.Velocity.X, 0, body.Velocity.Z) * dt;
        if (motion.LengthSquared() < 1e-6f) return 0f;
        // Look a little further ahead than one frame so slow walkers still find the step.
        if (motion.Length() < 0.08f) motion = motion.Normalized() * 0.08f;
        Transform3D from = body.GlobalTransform;
        var hit = new KinematicCollision3D();
        if (!body.TestMove(from, motion, hit)) { Why = "clear"; return 0f; }                   // nothing in the way
        if (hit.GetNormal().Y > 0.7f) { Why = $"slope n={hit.GetNormal()}"; return 0f; }       // just a slope
        Vector3 up = Vector3.Up * stepHeight;
        if (body.TestMove(from, up)) { Why = "headroom"; return 0f; }                          // no headroom
        Transform3D raised = from.Translated(up);
        Vector3 probe = motion.Normalized() * Mathf.Max(motion.Length(), ProbeAhead);
        if (body.TestMove(raised, probe)) { Why = "wall"; return 0f; }                         // a real wall
        var down = new KinematicCollision3D();
        if (!body.TestMove(raised.Translated(probe), -up, down)) { Why = "no ground"; return 0f; } // nothing to stand on
        if (down.GetNormal().Y < 0.7f) { Why = $"steep ground n={down.GetNormal()}"; return 0f; }
        float rise = stepHeight - down.GetTravel().Length();
        if (rise <= 0.02f) { Why = $"rise {rise}"; return 0f; }
        body.GlobalPosition += Vector3.Up * rise;
        Why = $"stepped {rise:0.00}";
        return rise;
    }
}
