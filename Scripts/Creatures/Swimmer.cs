using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.World;

namespace Solitude.Creatures;

/// <summary>
/// A data-driven sea creature (see CreatureProfile): loads Tom's rigged GLB, uses its albedo and
/// emission maps in the PS1 creature shader, animates the bones procedurally, and behaves as a
/// Grazer (curious, harmless) or an Apex (far-out circling, long passes, hull brushes, diver bites).
///
/// Interest: the creature pays attention to Focus (the diver if one is outside, else the boat).
/// </summary>
public partial class Swimmer : Node3D
{
    public CreatureProfile Profile;
    public TerrainGenerator Gen;
    public AudioDirector Audio;
    public Func<Vector3> Focus;              // what it is interested in
    public Func<bool> FocusIsDiver;
    public Func<float> DiverTimeOutside;
    public Func<bool> LightsOn;              // boat floods or diver lamp: grazers are drawn to light
    public Vector3 Anchor;                   // grazers wander around here

    public event Action<float> HullBrush;    // impact strength
    public event Action<float> BiteDiver;    // damage
    public bool Leaving { get; private set; }

    private enum State { Wander, Curious, Lurk, Approach, Pass }
    private State _state;
    private Skeleton3D _skel;
    private ShaderMaterial _mat;
    private readonly Dictionary<string, (int idx, Quaternion rest, Vector3 up, Vector3 right, Vector3 fwd)> _bones = new();
    private Vector3 _fwd = Vector3.Forward;
    private float _speed, _phase, _yawRate, _bank, _jaw, _t, _timer, _orbit, _biteCd;
    private Vector3 _target, _passStart, _passCross, _passEnd;
    private bool _passAimsClose;
    private int _passes;
    private readonly Random _rng = new();

    public float DistanceToFocus => Focus != null ? GlobalPosition.DistanceTo(Focus()) : float.MaxValue;

    public override void _Ready()
    {
        var model = GD.Load<PackedScene>(Profile.Scene).Instantiate<Node3D>();
        var pivot = new Node3D { RotationDegrees = new Vector3(0, 180, 0) }; // model faces +Z, Godot forward is -Z
        AddChild(pivot);
        pivot.AddChild(model);
        model.Scale = Vector3.One * (Profile.Length / Profile.ModelLength);

        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/creature.gdshader") };
        foreach (var mi in FindAll<MeshInstance3D>(model))
        {
            if (mi.Mesh?.SurfaceGetMaterial(0) is BaseMaterial3D src)
            {
                _mat.SetShaderParameter("albedo_tex", src.AlbedoTexture);
                _mat.SetShaderParameter("mask_tex", src.AlbedoTexture);
                if (src.EmissionTexture != null)
                {
                    _mat.SetShaderParameter("use_emission_tex", true);
                    _mat.SetShaderParameter("emission_tex", src.EmissionTexture);
                    _mat.SetShaderParameter("emission_tex_energy", Profile.EmissionEnergy);
                }
            }
            mi.MaterialOverride = _mat;
        }
        _mat.SetShaderParameter("eye_energy", 0f); // derived-eye glow is for the ravager only

        _skel = FindAll<Skeleton3D>(model).GetEnumerator() is var e && e.MoveNext() ? e.Current : null;
        if (_skel != null)
            for (int i = 0; i < _skel.GetBoneCount(); i++)
            {
                Basis inv = _skel.GetBoneGlobalRest(i).Basis.Orthonormalized().Inverse();
                _bones[_skel.GetBoneName(i)] = (i, _skel.GetBoneRest(i).Basis.GetRotationQuaternion(),
                    (inv * Vector3.Up).Normalized(), (inv * Vector3.Right).Normalized(), (inv * Vector3.Back).Normalized());
            }

        _speed = Profile.CruiseSpeed;
        _state = Profile.Temperament == Temperament.Apex ? State.Lurk : State.Wander;
        _timer = Profile.Temperament == Temperament.Apex ? 15f + (float)_rng.NextDouble() * 20f : 0f;
        _orbit = (float)_rng.NextDouble() * Mathf.Tau;
        _fwd = new Vector3(MathF.Sin(_orbit), 0, MathF.Cos(_orbit));
    }

    private static IEnumerable<T> FindAll<T>(Node n) where T : Node
    {
        if (n is T t) yield return t;
        foreach (var c in n.GetChildren()) foreach (var r in FindAll<T>(c)) yield return r;
    }

    /// <summary>Debug / scripted: start a pass at the focus right now.</summary>
    public void ForcePass(bool teleport = false)
    {
        BeginPass();
        if (teleport) { GlobalPosition = _passStart; _fwd = (_passCross - _passStart).Normalized(); _state = State.Pass; }
    }

    /// <summary>Apex: leave for good after a while (the director despawns it out of sight).</summary>
    public void Leave() => Leaving = true;

    private void BeginPass()
    {
        Vector3 f = Focus();
        float a = (float)_rng.NextDouble() * Mathf.Tau;
        Vector3 across = new(MathF.Cos(a), 0, MathF.Sin(a));
        Vector3 side = new(-across.Z, 0, across.X);
        _passAimsClose = _rng.NextDouble() < Profile.HullBrushChance;
        float off = _passAimsClose ? Profile.Length * 0.25f : Profile.PassDistance * (0.8f + (float)_rng.NextDouble() * 0.5f);
        _passCross = f + side * off + Vector3.Up * (float)(_rng.NextDouble() * 10 - 4);
        _passStart = _passCross - across * (Profile.Length * 2.5f + 40f);
        _passEnd = _passCross + across * (Profile.Length * 3f + 50f);
        _state = State.Approach;
        _passes++;
        Audio?.PlayCall();
    }

    public override void _Process(double delta)
    {
        float dt = (float)Math.Min(delta, 0.1);
        _t += dt;
        if (Focus == null) return;
        Vector3 pos = GlobalPosition;
        Vector3 f = Focus();
        float dist = pos.DistanceTo(f);
        float targetSpeed = Profile.CruiseSpeed;
        _biteCd -= dt;

        switch (_state)
        {
            // ---------------------------------------------------------------- grazer
            case State.Wander:
                _timer -= dt;
                if (_timer <= 0f || pos.DistanceTo(_target) < 4f)
                {
                    float a = (float)_rng.NextDouble() * Mathf.Tau, r = (float)_rng.NextDouble() * Profile.WanderRadius;
                    var p = Anchor + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                    float floor = Gen?.Height(p.X, p.Z) ?? p.Y;
                    _target = new Vector3(p.X, floor + 4f + (float)_rng.NextDouble() * 10f, p.Z);
                    _timer = 12f + (float)_rng.NextDouble() * 10f;
                }
                if (dist < Profile.CuriosityRange && (LightsOn?.Invoke() ?? true) && _rng.NextDouble() < dt * 0.15) { _state = State.Curious; _timer = 18f + (float)_rng.NextDouble() * 14f; }
                break;
            case State.Curious:
                // Lazy circles around the light, a little above it, then lose interest.
                _orbit += dt * Profile.CruiseSpeed / Profile.CuriousOrbit;
                _target = f + new Vector3(MathF.Sin(_orbit), 0.25f, MathF.Cos(_orbit)) * Profile.CuriousOrbit;
                _timer -= dt;
                if (_timer <= 0f) { _state = State.Wander; _timer = 0f; Anchor = pos; }
                break;

            // ---------------------------------------------------------------- apex
            case State.Lurk:
                _orbit += dt * Profile.CruiseSpeed / Profile.OrbitRadius;
                _target = f + new Vector3(MathF.Sin(_orbit), 0, MathF.Cos(_orbit)) * Profile.OrbitRadius + Vector3.Up * MathF.Sin(_t * 0.05f) * 12f;
                _timer -= dt;
                if (Leaving) _target = f + (pos - f).Normalized() * 600f;
                else if (_timer <= 0f) BeginPass();
                break;
            case State.Approach:
                _target = _passStart;
                targetSpeed = Profile.PassSpeed;
                if (pos.DistanceTo(_passStart) < Profile.Length) _state = State.Pass;
                break;
            case State.Pass:
                bool before = (_passCross - pos).Dot(_passEnd - _passCross) > 0 && pos.DistanceTo(_passCross) > Profile.Length * 0.3f;
                _target = before ? _passCross : _passEnd;
                targetSpeed = Profile.PassSpeed;
                if (pos.DistanceTo(_passEnd) < Profile.Length) { _state = State.Lurk; _timer = Profile.PassIntervalMin + (float)_rng.NextDouble() * (Profile.PassIntervalMax - Profile.PassIntervalMin); }
                // Contact: the body is long, so measure to the nearest point on the spine segment.
                Vector3 head = pos - GlobalBasis.Z * Profile.Length * 0.45f, tail = pos + GlobalBasis.Z * Profile.Length * 0.45f;
                float spine = DistanceToSegment(f, head, tail);
                bool diver = FocusIsDiver?.Invoke() ?? false;
                if (_biteCd <= 0f)
                {
                    if (diver && head.DistanceTo(f) < Profile.Length * 0.18f && (DiverTimeOutside?.Invoke() ?? 0f) > Profile.DiverBiteAfter)
                    { BiteDiver?.Invoke(Profile.DiverBite); _biteCd = 30f; }
                    else if (!diver && _passAimsClose && spine < 9f)
                    { HullBrush?.Invoke(1.2f + (9f - spine) * 0.15f); _biteCd = 30f; }
                }
                break;
        }

        // Steering: limited turn rate, limited pitch, keep clear of the seabed.
        Vector3 desired = (_target - pos).Normalized();
        if (Gen != null)
        {
            Vector3 ahead = pos + _fwd * Mathf.Max(20f, Profile.Length);
            if (ahead.Y < Gen.Height(ahead.X, ahead.Z) + Profile.Length * 0.15f + 3f) desired = (desired + Vector3.Up * 1.3f).Normalized();
        }
        Vector3 oldFlat = new Vector3(_fwd.X, 0, _fwd.Z).Normalized();
        float angle = _fwd.AngleTo(desired);
        if (angle > 1e-4f) _fwd = _fwd.Slerp(desired, Math.Min(1f, Profile.TurnRate * dt / angle)).Normalized();
        _fwd.Y = Math.Clamp(_fwd.Y, -0.35f, 0.35f);
        _fwd = _fwd.Normalized();
        Vector3 newFlat = new Vector3(_fwd.X, 0, _fwd.Z).Normalized();
        _yawRate = Mathf.Lerp(_yawRate, oldFlat.SignedAngleTo(newFlat, Vector3.Up) / Mathf.Max(dt, 1e-4f), 3f * dt);
        _speed = Mathf.MoveToward(_speed, targetSpeed, 1.0f * dt);
        pos += _fwd * _speed * dt;
        if (Gen != null) pos.Y = Math.Max(pos.Y, Gen.Height(pos.X, pos.Z) + 2f);
        _bank = Mathf.Lerp(_bank, Math.Clamp(-_yawRate * 0.9f, -0.45f, 0.45f), 2f * dt);
        // Serpents snake: the whole body's heading sways with the swim wave (the path stays smooth).
        float sway = Profile.BodySway * MathF.Sin(_phase);
        GlobalTransform = new Transform3D(Basis.LookingAt(_fwd, Vector3.Up) * new Basis(Vector3.Up, sway) * new Basis(Vector3.Forward, _bank), pos);

        Animate(dt, dist);
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Math.Clamp((p - a).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-4f), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    private void Animate(float dt, float distToFocus)
    {
        if (_skel == null) return;
        _phase += dt * (Profile.CycleBase + _speed * Profile.CyclePerSpeed) * Mathf.Tau;
        float curl = Math.Clamp(_yawRate * 0.9f, -0.5f, 0.5f);
        var rot = new Dictionary<string, Quaternion>();
        Quaternion Get(string b) => rot.TryGetValue(b, out var q) ? q : Quaternion.Identity;

        foreach (var (bone, amp, ph, c) in Profile.Yaw)
            if (_bones.TryGetValue(bone, out var bi)) rot[bone] = Get(bone) * new Quaternion(bi.up, amp * MathF.Sin(_phase - ph) + curl * c);
        foreach (var (bone, amp, ph) in Profile.Pitch)
            if (_bones.TryGetValue(bone, out var bi)) rot[bone] = Get(bone) * new Quaternion(bi.right, amp * MathF.Sin(_phase - ph));
        foreach (var (bone, amp, ph) in Profile.Roll)
            if (_bones.TryGetValue(bone, out var bi)) rot[bone] = Get(bone) * new Quaternion(bi.fwd, amp * MathF.Sin(_phase - ph));
        if (Profile.Jaw != null && _bones.TryGetValue(Profile.Jaw, out var jb))
        {
            float open = 0.05f + 0.04f * MathF.Sin(_t * 0.7f) + (_state == State.Pass && distToFocus < Profile.Length * 1.5f ? Profile.JawOpen : 0f);
            _jaw = Mathf.Lerp(_jaw, open, 2f * dt);
            rot[Profile.Jaw] = Get(Profile.Jaw) * new Quaternion(jb.right, _jaw);
        }
        foreach (var (bone, q) in rot)
        {
            var bi = _bones[bone];
            _skel.SetBonePoseRotation(bi.idx, bi.rest * q);
        }
    }
}
