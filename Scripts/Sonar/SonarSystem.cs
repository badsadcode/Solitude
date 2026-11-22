using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Solitude.Audio;
using Solitude.Sub;
using Solitude.World;

namespace Solitude.Sonar;

/// <summary>One echo on the PPI scope, in world XZ.</summary>
public struct SonarContact
{
    public Vector2 World;
    public double RevealAt;   // when the wavefront reaches it
    public float Strength;    // 0..1
    public bool Danger;       // terrain at or above the keel
    public bool Ghost;        // ...not terrain
}

/// <summary>
/// Active sonar. A ping casts 360 beams over the seabed heightfield (worker thread),
/// with acoustic shadowing: a cell is only heard if nothing nearer blocks the line of
/// sight. Results feed the PPI scope (contacts) and the persistent SonarMap (holo table).
/// The visual/aural wavefront travels at a dramatised 300 m/s.
/// </summary>
public partial class SonarSystem : Node
{
    public TerrainGenerator Gen;
    public Submarine Sub;
    public AudioDirector Audio;
    /// <summary>Positions of things that swim (creatures). Sampled at ping time.</summary>
    public Func<IEnumerable<Vector3>> Bodies;

    public static readonly float[] Ranges = { 150f, 300f, 500f, 800f };
    public int RangeIndex = 1;
    public float Range => Ranges[RangeIndex];
    /// <summary>Long-range transducer upgrade unlocks the 800 m setting.</summary>
    public bool LongRange;
    /// <summary>0..1: silt in the water scatters the ping (shorter reach). Set by the director.</summary>
    public float Clutter;

    public const float WaveSpeed = 300f;
    public const float Recharge = 4f;
    public const float ContactLife = 14f;

    public readonly SonarMap Map = new();
    public readonly List<SonarContact> Contacts = new();

    public double PingStart { get; private set; } = -999;
    public Vector3 PingOrigin { get; private set; }
    public float PingRange { get; private set; }
    public int PingCount { get; private set; }

    public event Action Pinged;

    public static double Now => Time.GetTicksMsec() / 1000.0;
    public float FrontRadius => (float)((Now - PingStart) * WaveSpeed);
    public bool WaveActive => FrontRadius < PingRange;
    public float RechargeLeft => (float)Math.Max(0, Recharge - (Now - PingStart));

    private Task<PingResult> _task;
    private readonly Random _rng = new();

    private sealed class PingResult
    {
        public List<SonarContact> Contacts;
        public List<(Vector2I cell, float height, double revealAt)> Cells;
    }

    /// <summary>A cluster of ghost returns at a world point, revealed at a given time (the Listener answering).</summary>
    public void AddPhantom(Vector2 world, double revealAt, int count, float spread)
    {
        for (int i = 0; i < count; i++)
            Contacts.Add(new SonarContact
            {
                World = world + new Vector2((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f) * spread,
                RevealAt = revealAt,
                Strength = 0.9f,
                Ghost = true,
            });
    }

    public void CycleRange() => RangeIndex = (RangeIndex + 1) % (LongRange ? Ranges.Length : Ranges.Length - 1);

    /// <summary>Fire a ping. Returns false while recharging.</summary>
    public bool Ping()
    {
        if (RechargeLeft > 0f || Sub == null) return false;
        PingStart = Now;
        PingOrigin = Sub.GlobalPosition;
        PingRange = Range * (1f - 0.55f * Clutter);
        PingCount++;
        var gen = Gen;
        var origin = PingOrigin;
        float range = PingRange;
        double start = PingStart;
        int seed = _rng.Next();
        _task = Task.Run(() => Sample(gen, origin, range, start, seed));
        // Silt scatters the ping into false returns: faint blotches that are gone on the next ping.
        if (Clutter > 0.05f)
            for (int i = 0; i < (int)(30 * Clutter); i++)
            {
                float a = (float)_rng.NextDouble() * Mathf.Tau, d = 20f + (float)_rng.NextDouble() * range * 0.8f;
                Contacts.Add(new SonarContact
                {
                    World = new Vector2(origin.X + Mathf.Cos(a) * d, origin.Z + Mathf.Sin(a) * d),
                    RevealAt = start + d / WaveSpeed,
                    Strength = 0.35f + 0.35f * (float)_rng.NextDouble(),
                    Ghost = true,
                });
            }
        // Living things echo too: a tight, bright cluster the map never records.
        if (Bodies != null)
            foreach (var b in Bodies())
            {
                float d = new Vector2(b.X - origin.X, b.Z - origin.Z).Length();
                if (d > range || d < 1f) continue;
                for (int i = 0; i < 6; i++)
                    Contacts.Add(new SonarContact
                    {
                        World = new Vector2(b.X, b.Z) + new Vector2((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f) * 8f,
                        RevealAt = start + d / WaveSpeed,
                        Strength = 0.95f,
                        Ghost = true,
                    });
            }
        Audio?.PlayPing();
        Pinged?.Invoke();
        return true;
    }

    public override void _Process(double delta)
    {
        if (_task != null && _task.IsCompleted)
        {
            var t = _task;
            _task = null;
            if (t.IsCompletedSuccessfully) Merge(t.Result);
            else GD.PushError($"Sonar ping failed: {t.Exception?.GetBaseException().Message}");
        }

        double now = Now;
        Contacts.RemoveAll(c => now - c.RevealAt > ContactLife);
    }

    private void Merge(PingResult r)
    {
        Map.Reveal(r.Cells);
        Contacts.AddRange(r.Contacts);

        // Echoes: the strongest nearby hard returns, at distinct ranges, timed to the wavefront.
        if (Audio == null) return;
        var picks = new List<SonarContact>();
        foreach (var c in r.Contacts)
        {
            if (c.Ghost || c.Strength < 0.55f) continue;
            picks.Add(c);
        }
        picks.Sort((a, b) => a.RevealAt.CompareTo(b.RevealAt));
        double last = -1;
        int played = 0;
        foreach (var c in picks)
        {
            if (c.RevealAt - last < 0.25) continue;
            last = c.RevealAt;
            Audio.PlayEcho((float)Math.Max(0, c.RevealAt - Now), c.Strength * (1f - played * 0.2f));
            if (++played >= 4) break;
        }
    }

    // ------------------------------------------------------------------ worker thread

    private static PingResult Sample(TerrainGenerator gen, Vector3 origin, float range, double start, int seed)
    {
        const int azimuths = 360;
        const float step = 4f, bin = 8f;
        var rng = new Random(seed);
        var contacts = new List<SonarContact>(4096);
        var cells = new Dictionary<Vector2I, (float h, double t)>(8192);
        float oy = origin.Y;

        for (int a = 0; a < azimuths; a++)
        {
            float ang = Mathf.Tau * a / azimuths;
            // Bearing 0 = north (-Z), clockwise.
            float dx = MathF.Sin(ang), dz = -MathF.Cos(ang);
            float maxElev = -10f;
            int curBin = -1;
            SonarContact best = default;

            for (float r = step; r <= range; r += step)
            {
                float x = origin.X + dx * r, z = origin.Z + dz * r;
                float h = gen.Height(x, z);
                float elev = MathF.Atan2(h - oy, r);
                float rise = elev - maxElev;
                bool visible = elev >= maxElev - 0.003f;
                if (elev > maxElev) maxElev = elev;
                if (!visible) continue;

                double t = start + r / WaveSpeed;
                var cell = SonarMap.CellOf(x, z);
                if (!cells.ContainsKey(cell)) cells[cell] = (h, t);

                // Brightness: surfaces facing the beam (big rise) ring loud; flat bottom whispers.
                float s = Math.Clamp(0.1f + rise * 30f, 0f, 1f) * (1f - r / range * 0.45f);
                if (elev < -0.7f) s *= 0.5f; // straight down is outside the main fan
                bool danger = h > oy - 8f;
                if (danger) s = MathF.Max(s, 0.65f);

                int b = (int)(r / bin);
                if (b != curBin)
                {
                    if (best.Strength > 0.12f) contacts.Add(best);
                    curBin = b;
                    best = default;
                }
                if (s > best.Strength)
                    best = new SonarContact { World = new Vector2(x, z), RevealAt = t, Strength = s, Danger = danger };
            }
            if (best.Strength > 0.12f) contacts.Add(best);
        }

        // Sometimes something answers that isn't the seabed.
        if (rng.NextDouble() < 0.12)
        {
            float ang = (float)rng.NextDouble() * Mathf.Tau;
            float r = range * (0.5f + (float)rng.NextDouble() * 0.45f);
            var p = new Vector2(origin.X + MathF.Sin(ang) * r, origin.Z - MathF.Cos(ang) * r);
            for (int i = 0; i < 5; i++)
                contacts.Add(new SonarContact
                {
                    World = p + new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 10f,
                    RevealAt = start + r / WaveSpeed,
                    Strength = 0.95f,
                    Ghost = true,
                });
        }

        var cellList = new List<(Vector2I, float, double)>(cells.Count);
        foreach (var kv in cells) cellList.Add((kv.Key, kv.Value.h, kv.Value.t));
        return new PingResult { Contacts = contacts, Cells = cellList };
    }
}
