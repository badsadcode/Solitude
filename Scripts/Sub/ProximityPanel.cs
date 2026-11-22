using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Sub;

/// <summary>
/// The six-light proximity panel (an idea from BALE IX). One lamp per hull camera direction:
/// BOW, PORT, STBD, KEEL, STERN, MAST (above). No numbers, no screen.
///
///   amber, steady  = something solid is close that way (rock, wreck, station). Brighter = closer;
///                    it starts to flicker when you're about to hit it.
///   red, blinking  = something MOVING is out there that way. Faster = closer.
///
/// The panel never says what. That's the job of the camera with the same number.
/// </summary>
public partial class ProximityPanel : Node
{
    public const int Count = 6;
    public static readonly string[] Labels = { "BOW", "PORT", "STBD", "KEEL", "STERN", "MAST" };

    /// <summary>How far each lamp looks for solid things (m). The bow sees furthest: that's where you're going.</summary>
    private static readonly float[] SolidRange = { 45f, 25f, 25f, 18f, 30f, 20f };
    public const float ContactRange = 70f;

    public Submarine Sub;
    /// <summary>Everything that moves out there (the sonar's body list).</summary>
    public Func<IEnumerable<Vector3>> Bodies;
    public bool Active = true;

    /// <summary>0..1 nearness of a solid obstacle, per lamp.</summary>
    public readonly float[] Solid = new float[Count];
    /// <summary>0..1 nearness of the closest moving contact, per lamp.</summary>
    public readonly float[] Contact = new float[Count];
    /// <summary>What the lamp shows this frame: colour and emission energy (0 = dark).</summary>
    public readonly Color[] LampColor = new Color[Count];
    public readonly float[] LampEnergy = new float[Count];
    /// <summary>Lamp index → a red lamp just came on (first contact), or blinked on while very close.</summary>
    public event Action<int, bool> Tick;

    public static readonly Color Amber = new(1f, 0.4f, 0.03f);
    public static readonly Color Red = new(1f, 0.08f, 0.05f);

    private readonly float[] _phase = new float[Count];
    private readonly bool[] _wasOn = new bool[Count];
    private readonly bool[] _hadContact = new bool[Count];
    private float _senseT;

    public override void _PhysicsProcess(double delta)
    {
        if (Sub == null || !Active) return;
        _senseT -= (float)delta;
        if (_senseT > 0f) return;
        _senseT = 0.12f;
        SenseSolid();
        SenseContacts();
    }

    private void SenseSolid()
    {
        var space = Sub.GetWorld3D().DirectSpaceState;
        Transform3D st = Sub.GlobalTransform;
        for (int i = 0; i < Count; i++)
        {
            var (pos, dir) = SubmarineModel.CameraPods[i];
            float range = SolidRange[i];
            // A small fan of three rays, so the lamp covers a cone, not a pencil line.
            Vector3 side = Mathf.Abs(dir.Y) > 0.9f ? Vector3.Right : Vector3.Up;
            float best = 0f;
            for (int r = -1; r <= 1; r++)
            {
                Vector3 d = dir.Rotated(side, r * 0.4f);
                Vector3 from = st * (pos + dir * 0.6f);
                Vector3 to = st * (pos + d * range);
                var q = PhysicsRayQueryParameters3D.Create(from, to, Layers.World);
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;
                float dist = from.DistanceTo((Vector3)hit["position"]);
                best = Mathf.Max(best, 1f - dist / range);
            }
            Solid[i] = best;
        }
    }

    private void SenseContacts()
    {
        Array.Clear(Contact);
        if (Bodies == null) return;
        Transform3D inv = Sub.GlobalTransform.AffineInverse();
        foreach (var b in Bodies())
        {
            Vector3 l = inv * b;
            float d = l.Length();
            if (d > ContactRange || d < 0.01f) continue;
            // The pod with the most "this way" wins. The keel/mast lamps cover a narrower cone
            // (the boat is long and flat: most things are beside it, not under it).
            float best = -2f; int lamp = 0;
            for (int i = 0; i < Count; i++)
            {
                float dot = l.Normalized().Dot(SubmarineModel.CameraPods[i].dir);
                if (i is 3 or 5) dot -= 0.15f;
                if (dot > best) { best = dot; lamp = i; }
            }
            Contact[lamp] = Mathf.Max(Contact[lamp], 1f - d / ContactRange);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = 0; i < Count; i++)
        {
            bool on;
            if (!Active) { LampEnergy[i] = 0f; _wasOn[i] = false; _hadContact[i] = false; continue; }
            if (Contact[i] > 0f)
            {
                // 1 Hz at the edge of range, ~6 Hz when it's right on top of you.
                float hz = Mathf.Lerp(1f, 6f, Contact[i] * Contact[i]);
                _phase[i] = (_phase[i] + hz * dt) % 1f;
                on = _phase[i] < 0.5f;
                LampColor[i] = Red;
                LampEnergy[i] = on ? 1.6f + Contact[i] * 2.4f : 0.05f;
                if (!_hadContact[i]) { _phase[i] = 0f; on = true; Tick?.Invoke(i, true); }
                else if (on && !_wasOn[i] && Contact[i] > 0.6f) Tick?.Invoke(i, false);
                _hadContact[i] = true;
            }
            else
            {
                _hadContact[i] = false;
                float s = Solid[i];
                if (s > 0f)
                {
                    LampColor[i] = Amber;
                    LampEnergy[i] = 0.4f + s * 2.6f;
                    if (s > 0.8f) // about to hit it: a nervous flutter
                    {
                        _phase[i] = (_phase[i] + 9f * dt) % 1f;
                        if (_phase[i] < 0.3f) LampEnergy[i] *= 0.3f;
                    }
                }
                else LampEnergy[i] = 0f;
                on = LampEnergy[i] > 0.1f;
            }
            _wasOn[i] = on;
        }
    }
}
