using Godot;

namespace Solitude.Core;

/// <summary>
/// Stair stepping for CharacterBody3D (players and creatures). Godot's MoveAndSlide treats a
/// step as a wall; this lifts the body onto it when there's headroom and ground beyond.
/// Call right before MoveAndSlide with the velocity you're about to apply.
/// </summary>
public static class Stepper
{
    public static bool TryStepUp(CharacterBody3D body, float dt, float stepHeight)
    {
        if (!body.IsOnFloor()) return false;
        Vector3 motion = new Vector3(body.Velocity.X, 0, body.Velocity.Z) * dt;
        if (motion.LengthSquared() < 1e-6f) return false;
        // Look a little further ahead than one frame so slow walkers still find the step.
        if (motion.Length() < 0.08f) motion = motion.Normalized() * 0.08f;
        Transform3D from = body.GlobalTransform;
        var hit = new KinematicCollision3D();
        if (!body.TestMove(from, motion, hit)) return false;         // nothing in the way
        if (hit.GetNormal().Y > 0.7f) return false;                    // just a slope
        Vector3 up = Vector3.Up * stepHeight;
        if (body.TestMove(from, up)) return false;                     // no headroom
        Transform3D raised = from.Translated(up);
        if (body.TestMove(raised, motion)) return false;               // a real wall
        var down = new KinematicCollision3D();
        if (!body.TestMove(raised.Translated(motion), -up, down)) return false; // nothing to stand on
        if (down.GetNormal().Y < 0.7f) return false;
        float rise = stepHeight - down.GetTravel().Length();
        if (rise <= 0.02f) return false;
        body.GlobalPosition += Vector3.Up * rise;
        return true;
    }
}
