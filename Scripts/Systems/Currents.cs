using System;
using Godot;
using Solitude.Core;

namespace Solitude.Systems;

/// <summary>
/// The water moves. A slow current field over the whole map (an idea from BALE IX):
///
///  * a gentle background drift everywhere (a few cm/s up to ~0.25 m/s), its direction wandering
///    across kilometres and turning slowly over time;
///  * "rivers": narrow bands where the noise ridges line up and the current runs much faster
///    (up to ~0.9 m/s), the kind of thing that carries a stopped boat off its mark;
///  * a slow tide that swells and slackens everything over a few minutes;
///  * a gust from a silt storm on top, when there is one (set by the director).
///
/// Pure function of position and time (plus the gust), so the boat, the diver, the particles and
/// the helm readout all agree. Nothing here is saved: the field is the same for the same seed.
/// </summary>
public sealed class Currents
{
    private readonly Perlin _dir, _speed, _river;

    /// <summary>Silt-storm push (world m/s), added everywhere near the focus. Set by the director.</summary>
    public Vector3 Gust;

    public Currents(int seed)
    {
        _dir = new Perlin(seed + 101);
        _speed = new Perlin(seed + 102);
        _river = new Perlin(seed + 103);
    }

    /// <summary>Water velocity (m/s, world) at p and time t (seconds).</summary>
    public Vector3 At(Vector3 p, float t)
    {
        float x = p.X, z = p.Z;
        // Direction: a smooth angle field that drifts with time.
        float a = _dir.Fbm(x / 2400f + t * 0.0004f, z / 2400f, 2) * Mathf.Pi * 2.2f;
        Vector3 dir = new(Mathf.Sin(a), 0, Mathf.Cos(a));

        float tide = 0.75f + 0.25f * Mathf.Sin(t * Mathf.Tau / 360f);   // six-minute swell and slack
        float bg = 0.04f + 0.21f * Mathf.Clamp(_speed.Fbm(x / 1600f, z / 1600f + 7.3f, 2) * 0.5f + 0.5f, 0f, 1f);

        // Rivers: where a ridged noise peaks (thin bands kilometres long), the water runs fast,
        // along the band rather than across it.
        float ridge = 1f - Mathf.Abs(_river.Fbm(x / 1300f, z / 1300f, 2));
        float river = Mathf.Clamp((ridge - 0.86f) / 0.14f, 0f, 1f);
        river *= river;
        if (river > 0f)
        {
            // Along the band: perpendicular to the noise gradient.
            float e = 6f;
            float gx = (1f - Mathf.Abs(_river.Fbm((x + e) / 1300f, z / 1300f, 2))) - ridge;
            float gz = (1f - Mathf.Abs(_river.Fbm(x / 1300f, (z + e) / 1300f, 2))) - ridge;
            Vector3 along = new(-gz, 0, gx);
            if (along.LengthSquared() > 1e-10f)
            {
                along = along.Normalized();
                if (along.Dot(dir) < 0f) along = -along;   // keep it flowing the same way as the drift
                dir = dir.Lerp(along, river).Normalized();
            }
        }

        float speed = (bg + river * 0.7f) * tide;
        // Hard against the seabed the water is slower (boundary layer): handled by the callers
        // that know the floor; here just the free stream.
        return dir * speed + Gust;
    }

    /// <summary>"214°"-style set: the compass direction the water is flowing TOWARD (0 = -Z).</summary>
    public static float SetDegrees(Vector3 v) => Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(v.X, -v.Z)), 360f);
}
