using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.Core;
using Solitude.World;

namespace Solitude.Creatures;

/// <summary>
/// THE GLOAM (working name; an idea from BALE IX's "Hunters": a thing you cannot fight, only avoid).
///
/// A 24 m armoured, segmented swimmer that rows itself through the water on paired paddles. It is
/// drawn to LIGHT, not sound (that's the Listener's job): the boat's floods, the diver's lamp.
///
///   Prowl   circles far out. If it sees light within its sight range, it turns: Drawn.
///           In the dark it only finds you by blundering into you (close, and you moving fast).
///   Drawn   comes straight for the light, faster the brighter it is.
///   Search  the light went out: it goes to where it last saw it and circles there, drifting toward
///           the real boat only if the boat keeps moving. Gives up after a while.
///   Strike  a lunge at the light. One hit, then it peels off (Recoil) and comes round again.
///   Leave   after enough searching it loses interest and swims off.
///
/// The spear does nothing to it (it gets angrier). The shock prod makes it flinch for a few seconds.
/// Placeholder body built procedurally (like Sage's crawler) until Tom models one: see MODELS.md.
/// </summary>
public partial class Gloam : Node3D, IHarpoonable
{
    public TerrainGenerator Gen;
    public AudioDirector Audio;
    /// <summary>Where the thing it hunts is (the boat's centre, or the diver).</summary>
    public Func<Vector3> Target;
    /// <summary>0..1: how much light the target shows right now.</summary>
    public Func<float> Brightness;
    /// <summary>The target's speed (m/s): moving in the dark still gives you away up close.</summary>
    public Func<float> TargetSpeed;
    public Func<bool> TargetIsDiver;

    /// <summary>It hit the target. Argument = strength (1 = a solid hit).</summary>
    public event Action<float> Struck;
    /// <summary>It just noticed the light (state went to Drawn from Prowl/Search).</summary>
    public event Action Noticed;
    /// <summary>It lost the light and started searching.</summary>
    public event Action LostYou;

    public enum State { Prowl, Drawn, Search, Strike, Recoil, Leave }
    public State Current { get; private set; } = State.Prowl;
    public bool Leaving => Current == State.Leave;
    public float DistanceToTarget => Target == null ? float.MaxValue : _seg[0].DistanceTo(Target());

    public const float Length = 24f;
    private const int Segments = 14;
    private const float Spacing = Length / Segments;

    private readonly Vector3[] _seg = new Vector3[Segments];
    private readonly Node3D[] _nodes = new Node3D[Segments];
    private readonly List<(Node3D pivot, int seg, int side)> _paddles = new();
    private Vector3 _vel;
    private float _t, _stateT, _darkT, _searchStep, _stunT, _angerT, _paddlePh;
    private Vector3 _lastSeen, _searchCentre, _recoilTo;
    private int _losses;
    private bool _hitDone;
    private ShaderMaterial _eyeMat;
    private readonly Random _rng = new();

    public IEnumerable<Vector3> SonarPoints()
    {
        for (int i = 0; i < Segments; i += 3) yield return _seg[i];
    }

    // ------------------------------------------------------------------ IHarpoonable

    public Vector3 Head => _seg[0];
    public Vector3 Tail => _seg[Segments - 1];
    public float BodyRadius => 1.5f;
    public Node3D Body => _nodes[3];

    public void Hurt(float damage, Vector3 from, bool shock)
    {
        if (shock)
        {
            // The only thing that works: it flinches away for a few seconds.
            _stunT = 3.5f;
            Go(State.Recoil);
            _recoilTo = _seg[0] + (_seg[0] - from).Normalized() * 60f;
            Audio?.PlayAt("screech", 0f, 0.7f);
        }
        else
        {
            // The spear skips off the plates. It doesn't slow. It does notice.
            _angerT = 14f;
            _lastSeen = from;
            if (Current is State.Prowl or State.Search) { Go(State.Drawn); Noticed?.Invoke(); }
            Audio?.PlayAt("gloam", 0f, 0.9f);
        }
    }

    // ------------------------------------------------------------------ spawn

    /// <summary>Place it (all segments in a line behind the head, pointing along dir).</summary>
    public void Place(Vector3 at, Vector3 dir)
    {
        dir = dir.LengthSquared() > 1e-4f ? dir.Normalized() : Vector3.Forward;
        for (int i = 0; i < Segments; i++) _seg[i] = at - dir * Spacing * i;
        _vel = dir * 3f;
        _lastSeen = at;
        Pose();
    }

    public void Leave() => Go(State.Leave);

    /// <summary>Debug / capture: go straight for the target.</summary>
    public void Provoke() { _lastSeen = Target?.Invoke() ?? _seg[0]; Go(State.Drawn); }

    public override void _Ready()
    {
        BuildBody();
    }

    private void Go(State s)
    {
        Current = s;
        _stateT = 0f;
        _hitDone = false;
        if (s == State.Search) { _searchCentre = _lastSeen; _searchStep = 6f; }
    }

    // ------------------------------------------------------------------ behaviour

    public override void _Process(double delta)
    {
        if (Target == null) return;
        float dt = (float)delta;
        _t += dt; _stateT += dt;
        _stunT -= dt; _angerT -= dt;
        Vector3 tgt = Target();
        Vector3 head = _seg[0];
        float dist = head.DistanceTo(tgt);
        float bright = Brightness?.Invoke() ?? 0f;
        bool diver = TargetIsDiver?.Invoke() ?? false;
        float sight = bright <= 0f ? 0f : (diver ? 45f : 70f) + 160f * bright;
        bool sees = bright > 0f && dist < sight;
        // Blundering into you: close, in the dark, while you're moving.
        bool bumps = dist < (diver ? 10f : 24f) && (TargetSpeed?.Invoke() ?? 0f) > (diver ? 1.2f : 1.4f);
        if (sees || _angerT > 0f) _lastSeen = tgt;

        Vector3 goal; float speed;
        switch (Current)
        {
            case State.Prowl:
            {
                // A slow, wide circle around the target, far enough out that the floods don't reach it.
                float a = _t * 0.025f;
                goal = tgt + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 150f + Vector3.Up * 6f * MathF.Sin(_t * 0.07f);
                speed = 3f;
                if (sees || bumps) { Go(State.Drawn); Noticed?.Invoke(); }
                break;
            }
            case State.Drawn:
            {
                goal = _lastSeen;
                speed = 5f + 4f * bright;
                if (!sees && !bumps && _angerT <= 0f) _darkT += dt; else _darkT = 0f;
                if (_darkT > 1.5f) { _darkT = 0f; Go(State.Search); LostYou?.Invoke(); }
                else if (dist < (diver ? 14f : 30f) && (sees || bumps || _angerT > 0f)) Go(State.Strike);
                break;
            }
            case State.Search:
            {
                // Circle where the light was. Every few seconds, if the target is moving, it drifts that way.
                _searchStep -= dt;
                if (_searchStep <= 0f)
                {
                    _searchStep = 6f;
                    if ((TargetSpeed?.Invoke() ?? 0f) > 1f) _searchCentre = _searchCentre.Lerp(tgt, 0.3f);
                }
                float a = _t * 0.22f;
                goal = _searchCentre + new Vector3(MathF.Cos(a), 0.15f * MathF.Sin(a * 2f), MathF.Sin(a)) * 26f;
                speed = 3.5f;
                if (sees || bumps) { Go(State.Drawn); Noticed?.Invoke(); }
                else if (_stateT > 40f)
                {
                    _losses++;
                    if (_losses >= 2) Go(State.Leave); else Go(State.Prowl);
                }
                break;
            }
            case State.Strike:
            {
                goal = tgt;
                speed = 13f;
                float reach = diver ? 3f : 12f;   // the boat is a 33 m hull: its side is ~3 m from the line, its ends 16 m
                if (!_hitDone && dist < reach) { _hitDone = true; Struck?.Invoke(diver ? 1f : 1f + 0.5f * bright); Audio?.PlayAt("gloam", 0f, 1f); }
                if (_hitDone || _stateT > 4f)
                {
                    Go(State.Recoil);
                    Vector3 away = head - tgt; away.Y = 0;
                    if (away.LengthSquared() < 1e-3f) away = -_vel;
                    _recoilTo = tgt + away.Normalized() * 80f + Vector3.Up * 10f;
                }
                break;
            }
            case State.Recoil:
            {
                goal = _recoilTo;
                speed = _stunT > 0f ? 4f : 9f;
                if (_stateT > 12f) Go(sees ? State.Drawn : State.Search);
                break;
            }
            default: // Leave
            {
                Vector3 away = head - tgt; away.Y = 0;
                goal = head + (away.LengthSquared() > 1e-3f ? away.Normalized() : Vector3.Forward) * 100f;
                speed = 6f;
                break;
            }
        }
        if (_stunT > 0f) speed *= 0.4f;

        // Steering: turn the velocity toward the goal (limited turn rate), never into the seabed.
        Vector3 want = goal - head;
        if (Gen != null)
        {
            float floor = Gen.Height(head.X, head.Z);
            if (head.Y < floor + 5f) want.Y += (floor + 5f - head.Y) * 6f;
        }
        if (want.LengthSquared() > 1e-4f)
        {
            Vector3 cur = _vel.LengthSquared() > 1e-4f ? _vel.Normalized() : want.Normalized();
            float turn = (Current == State.Strike ? 1.3f : 0.7f) * dt;
            Vector3 dir = cur.Slerp(want.Normalized(), Mathf.Clamp(turn / Mathf.Max(cur.AngleTo(want.Normalized()), 1e-3f), 0f, 1f));
            float s = Mathf.MoveToward(_vel.Length(), speed, 3f * dt);
            _vel = dir * s;
        }
        // A slow side-to-side weave, which the body chain turns into an S-curve.
        Vector3 side = _vel.Cross(Vector3.Up);
        if (side.LengthSquared() > 1e-4f) side = side.Normalized();
        _seg[0] = head + (_vel + side * MathF.Sin(_t * 1.1f) * 0.9f) * dt;

        // Follow-the-leader: every segment keeps its spacing behind the one in front.
        for (int i = 1; i < Segments; i++)
        {
            Vector3 d = _seg[i] - _seg[i - 1];
            if (d.LengthSquared() < 1e-6f) d = -_vel;
            _seg[i] = _seg[i - 1] + d.Normalized() * Spacing;
        }
        Pose();
        Animate(dt);
    }

    // ------------------------------------------------------------------ body

    private void Pose()
    {
        if (_nodes[0] == null) return;
        for (int i = 0; i < Segments; i++)
        {
            Vector3 fwd = i == 0 ? (_vel.LengthSquared() > 1e-4f ? _vel.Normalized() : _seg[0] - _seg[1]) : _seg[i - 1] - _seg[i];
            if (fwd.LengthSquared() < 1e-6f) fwd = Vector3.Forward;
            fwd = fwd.Normalized();
            Vector3 up = Mathf.Abs(fwd.Y) > 0.95f ? Vector3.Back : Vector3.Up;
            _nodes[i].GlobalTransform = new Transform3D(Basis.LookingAt(fwd, up), _seg[i]);
        }
    }

    private void Animate(float dt)
    {
        // Paddles row in a metachronal wave (back to front, like a centipede's legs), faster when it hurries.
        _paddlePh += (0.5f + _vel.Length() * 0.12f) * dt * Mathf.Tau;
        foreach (var (pivot, seg, side) in _paddles)
        {
            float ph = _paddlePh + seg * 0.55f;
            float sweep = MathF.Sin(ph) * 0.6f;                   // fore-aft stroke
            float lift = MathF.Max(0f, MathF.Cos(ph)) * 0.35f;    // feathered on the return
            pivot.Rotation = new Vector3(0, side * sweep, side * (0.15f + lift));
        }
        // The eyes: a dull ember while it prowls, wide open when it hunts.
        float want = Current is State.Drawn or State.Strike ? 2.2f : Current == State.Search ? 1.2f : 0.6f;
        float e = (float)_eyeMat.GetShaderParameter("emission_energy");
        _eyeMat.SetShaderParameter("emission_energy", Mathf.MoveToward(e, want, dt));
    }

    private void BuildBody()
    {
        // Dark, wet-looking plates. Eyes: vertex alpha < 1 glows (emission_from_alpha).
        var plate = Art.World(Art.PanelTexture(77, new Color(0.3f, 0.28f, 0.27f)), Colors.White, uvScale: 0.6f, roughness: 0.6f, jitter: 200f);
        _eyeMat = Art.World(Art.FlatTexture(new Color(0.2f, 0.2f, 0.2f)), Colors.White, roughness: 0.6f, jitter: 200f)
            .Glow(new Color(0.75f, 0.85f, 0.7f), 0.6f, fromAlpha: true);
        Color shell = new(0.22f, 0.2f, 0.19f), seam = new(0.08f, 0.07f, 0.07f), pale = new(0.5f, 0.47f, 0.42f);

        for (int i = 0; i < Segments; i++)
        {
            float u = i / (float)(Segments - 1);
            // Broadest just behind the head, tapering to a thin tail.
            float w = Mathf.Lerp(2.6f, 0.7f, Mathf.Pow(u, 0.8f)) * (i == 0 ? 1.15f : 1f);
            float h = w * 0.45f;
            var node = new Node3D { Name = $"Seg{i}" };
            AddChild(node);
            _nodes[i] = node;
            var mb = new MeshBuilder();
            // Each segment is an arched plate (like an isopod's), overlapping the next one back
            // (forward is -Z): a domed core, two bevelled flanks, a rim that flares out and down with a
            // spine at each end, a seam ridge along the back and a pale soft belly underneath.
            float len = Spacing * 1.25f;
            mb.Box(new Vector3(0, h * 0.1f, Spacing * 0.15f), new Vector3(w * 0.62f, h, len), shell);
            foreach (int s in new[] { -1, 1 })
            {
                mb.BoxOriented(new Transform3D(new Basis(Vector3.Back, -s * 0.55f), new Vector3(s * w * 0.36f, 0, Spacing * 0.15f)), new Vector3(w * 0.34f, h * 0.55f, len * 0.97f), shell.Darkened(0.1f));
                // Rim: flares out and slopes down toward the back edge.
                mb.BoxOriented(new Transform3D(new Basis(Vector3.Back, -s * 0.35f) * new Basis(Vector3.Right, -0.12f), new Vector3(s * w * 0.55f, -h * 0.3f, Spacing * 0.3f)), new Vector3(w * 0.3f, 0.09f, len * 0.85f), shell.Lightened(0.05f));
                // A short spine off the rim, swept back.
                mb.BoxOriented(new Transform3D(new Basis(Vector3.Up, s * 0.7f), new Vector3(s * w * 0.72f, -h * 0.38f, Spacing * 0.75f)), new Vector3(0.09f, 0.09f, 0.55f + w * 0.15f), pale.Darkened(0.2f));
            }
            mb.Box(new Vector3(0, h * 0.65f, Spacing * 0.1f), new Vector3(0.22f, h * 0.25f, len * 0.9f), seam);   // dorsal seam
            mb.Box(new Vector3(0, h * 0.62f, Spacing * 0.68f), new Vector3(w * 0.6f, 0.07f, 0.12f), seam);         // gap to the next plate
            mb.Box(new Vector3(0, -h * 0.42f, 0), new Vector3(w * 0.55f, 0.1f, Spacing * 1.0f), pale);           // pale belly
            if (i == 0)
            {
                // A flat, wide skull with a crown of six small eyes, and two long feelers ahead.
                // A shovel-shaped skull: a low wedge that slopes down to a blunt front edge,
                // with a pair of hooked mandibles folded underneath.
                mb.Box(new Vector3(0, -0.05f, -Spacing * 0.6f), new Vector3(w * 0.75f, h * 0.6f, Spacing * 0.7f), shell);
                mb.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.35f), new Vector3(0, h * 0.2f, -Spacing * 0.85f)), new Vector3(w * 0.85f, 0.12f, Spacing * 0.9f), shell.Lightened(0.06f));
                foreach (int s2 in new[] { -1, 1 })
                {
                    mb.BoxOriented(new Transform3D(new Basis(Vector3.Up, s2 * 0.45f), new Vector3(s2 * w * 0.22f, -h * 0.35f, -Spacing * 1.25f)), new Vector3(0.16f, 0.14f, 1.1f), pale.Darkened(0.15f));
                    mb.BoxOriented(new Transform3D(new Basis(Vector3.Up, -s2 * 0.9f), new Vector3(s2 * w * 0.05f, -h * 0.35f, -Spacing * 1.7f)), new Vector3(0.12f, 0.12f, 0.6f), pale.Darkened(0.25f));
                }
                for (int e = 0; e < 6; e++)
                {
                    float ex = (e % 3 - 1) * 0.42f * (e < 3 ? 1f : 0.75f);
                    float ey = e < 3 ? h * 0.32f : h * 0.12f;
                    mb.Box(new Vector3(ex, ey, -Spacing * 1.15f), new Vector3(0.14f, 0.12f, 0.06f), new Color(0.9f, 1f, 0.85f, 0.5f));
                }
                foreach (int s in new[] { -1, 1 })
                    mb.BoxOriented(new Transform3D(new Basis(Vector3.Up, s * 0.25f), new Vector3(s * 0.8f, -0.1f, -Spacing * 1.1f - 2.2f)), new Vector3(0.07f, 0.07f, 4.4f), pale);
            }
            node.AddChild(new MeshInstance3D { Mesh = mb.Build(i == 0 ? _eyeMat : plate) });

            // Paddles on every segment but the tail tip.
            if (i > 0 && i < Segments - 1)
            {
                foreach (int s in new[] { -1, 1 })
                {
                    var pivot = new Node3D { Position = new Vector3(s * w * 0.5f, -h * 0.25f, 0) };
                    node.AddChild(pivot);
                    var pb = new MeshBuilder();
                    // A long jointed oar: shaft, then a broad fringed blade.
                    float plen = Mathf.Lerp(3.4f, 1.2f, u);
                    pb.Box(new Vector3(s * plen * 0.3f, 0, 0), new Vector3(plen * 0.6f, 0.12f, 0.16f), pale.Darkened(0.3f));
                    pb.Box(new Vector3(s * plen * 0.78f, -0.05f, 0.05f), new Vector3(plen * 0.45f, 0.05f, Spacing * 0.5f), shell.Darkened(0.2f));
                    for (int k = 0; k < 3; k++)
                        pb.Box(new Vector3(s * plen * (0.62f + k * 0.14f), -0.06f, Spacing * 0.32f), new Vector3(0.05f, 0.04f, 0.35f), pale.Darkened(0.35f));
                    pivot.AddChild(new MeshInstance3D { Mesh = pb.Build(plate) });
                    _paddles.Add((pivot, i, s));
                }
            }
        }
        Pose();
    }
}
