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
public partial class Swimmer : Node3D, IHarpoonable
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
    /// <summary>Debug/capture: force the jaw angle.</summary>
    public float? JawOverride;
    /// <summary>The eye encounter: something for the eyes to look at, and a speed to hold.</summary>
    public Vector3? EyeTarget;
    public float? SpeedOverride;
    /// <summary>Hold a scripted line even close to the bottom (the eye encounter).</summary>
    public bool IgnoreSeabed;
    private readonly List<Node3D> _eyes = new();

    private enum State { Wander, Curious, Lurk, Approach, Pass, Transit }
    private Vector3 _transitTo;
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
        _mat.SetShaderParameter("wet_spec", Profile.WetSpec);
        _mat.SetShaderParameter("roughness_v", Profile.Roughness);

        _skel = FindAll<Skeleton3D>(model).GetEnumerator() is var e && e.MoveNext() ? e.Current : null;
        if (_skel != null)
            for (int i = 0; i < _skel.GetBoneCount(); i++)
            {
                Basis inv = _skel.GetBoneGlobalRest(i).Basis.Orthonormalized().Inverse();
                _bones[_skel.GetBoneName(i)] = (i, _skel.GetBoneRest(i).Basis.GetRotationQuaternion(),
                    (inv * Vector3.Up).Normalized(), (inv * Vector3.Right).Normalized(), (inv * Vector3.Back).Normalized());
            }

        if (Profile.EyeBone != null && _skel != null) BuildEyes();

        _speed = Profile.CruiseSpeed;
        _state = Profile.Temperament == Temperament.Apex ? State.Lurk : State.Wander;
        _timer = Profile.Temperament == Temperament.Apex ? 15f + (float)_rng.NextDouble() * 20f : 0f;
        _orbit = (float)_rng.NextDouble() * Mathf.Tau;
        _fwd = new Vector3(MathF.Sin(_orbit), 0, MathF.Cos(_orbit));
    }

    /// <summary>Two eyes riding the head bone: a dark wet orb, a faint pale iris, a pupil. They turn to look.</summary>
    private void BuildEyes()
    {
        int bi = _skel.FindBone(Profile.EyeBone);
        if (bi < 0) return;
        Transform3D boneRest = _skel.GetBoneGlobalRest(bi);
        float r = Profile.EyeRadius;
        var orb = new StandardMaterial3D { AlbedoColor = new Color(0.02f, 0.025f, 0.035f), Roughness = 0.04f, MetallicSpecular = 1f };
        var iris = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.5f, 0.3f), EmissionEnabled = true, Emission = new Color(0.7f, 0.62f, 0.35f), EmissionEnergyMultiplier = 0.5f, Roughness = 0.2f };
        var pupil = new StandardMaterial3D { AlbedoColor = Colors.Black, Roughness = 0.02f, MetallicSpecular = 1f };
        foreach (float side in new[] { -1f, 1f })
        {
            var att = new BoneAttachment3D { BoneName = Profile.EyeBone };
            _skel.AddChild(att);
            var socket = new Node3D { Transform = boneRest.AffineInverse() * new Transform3D(Basis.Identity, new Vector3(side * Profile.EyeModel.X, Profile.EyeModel.Y, Profile.EyeModel.Z)) };
            att.AddChild(socket);
            // The eye itself is top-level so it can turn freely and keep its size in metres.
            var eye = new Node3D { TopLevel = true, Name = side < 0 ? "EyeL" : "EyeR" };
            socket.AddChild(eye);
            eye.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = 2f * r, RadialSegments = 10, Rings = 6 }, MaterialOverride = orb });
            eye.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = r * 0.5f, Height = r * 1f, RadialSegments = 10, Rings = 4 }, MaterialOverride = iris, Position = new Vector3(0, 0, -r * 0.84f), Scale = new Vector3(1f, 1f, 0.35f) });
            eye.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = r * 0.22f, Height = r * 0.44f, RadialSegments = 8, Rings = 4 }, MaterialOverride = pupil, Position = new Vector3(0, 0, -r * 0.98f), Scale = new Vector3(1.6f, 1f, 0.3f) });
            _eyes.Add(eye);
        }
    }

    /// <summary>World position of an eye (side −1 left, +1 right), or the body if it has none.</summary>
    public Vector3 EyeWorld(int side) => _eyes.Count == 2 ? ((Node3D)_eyes[side < 0 ? 0 : 1].GetParent()).GlobalPosition : GlobalPosition;

    private void UpdateEyes(float dt)
    {
        foreach (var eye in _eyes)
        {
            var socket = (Node3D)eye.GetParent();
            Vector3 at = socket.GlobalPosition;
            // Rest: looking straight out of the side of the head. With a target: turning to it.
            Vector3 outward = at - GlobalPosition; outward -= GlobalBasis.Z * outward.Dot(GlobalBasis.Z); outward.Y = 0;
            Vector3 look = EyeTarget is Vector3 t && t.DistanceTo(at) < 120f ? (t - at) : outward;
            if (look.LengthSquared() < 1e-4f) look = Vector3.Forward;
            Basis want = Basis.LookingAt(look.Normalized(), Vector3.Up);
            Basis cur = eye.GlobalBasis.Orthonormalized();
            eye.GlobalTransform = new Transform3D(cur.Slerp(want, Mathf.Clamp(1.5f * dt, 0f, 1f)), at);
        }
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

    // ---------------------------------------------------------------- the diver's harpoon
    private float _fleeT, _stunT, _spears;
    private Vector3 _fleeFrom;
    public Vector3 Head => GlobalPosition - GlobalBasis.Z * Profile.Length * 0.45f;
    public Vector3 Tail => GlobalPosition + GlobalBasis.Z * Profile.Length * 0.45f;
    public float BodyRadius => Profile.Length * 0.09f;
    public Node3D Body => this;

    /// <summary>
    /// Grazers just swim away, offended. The apex shrugs off a spear or two — but a shock, or a third
    /// spear, makes it break off whatever it was doing and circle far out for a good while.
    /// </summary>
    public void Hurt(float damage, Vector3 from, bool shock)
    {
        _fleeFrom = from;
        if (Profile.Temperament != Temperament.Apex)
        {
            _fleeT = 10f; _state = State.Wander; _timer = 0f;
            Vector3 away = (GlobalPosition - from); away.Y = 0;
            Anchor = GlobalPosition + away.Normalized() * 70f;
            return;
        }
        if (shock) _stunT = 1.2f; else _spears++;
        if (shock || _spears >= 3)
        {
            _spears = 0;
            _fleeT = 8f;
            _biteCd = Math.Max(_biteCd, 40f);
            _state = State.Lurk;
            _timer = Profile.PassIntervalMax + (float)_rng.NextDouble() * 60f;
            Audio?.PlayAt("screech", 0f, 0.8f);
        }
    }

    /// <summary>Apex: leave for good after a while (the director despawns it out of sight).</summary>
    public void Leave() => Leaving = true;

    /// <summary>Move a transit's destination without snapping the heading (it steers there at its own pace).</summary>
    public void SetTransitTarget(Vector3 to) { if (_state == State.Transit) _transitTo = to; }

    /// <summary>Scripted flyby (title screen): glide toward `to`, then flag Leaving once there.</summary>
    public void Transit(Vector3 to)
    {
        _transitTo = to;
        _state = State.Transit;
        _fwd = (to - GlobalPosition).Normalized();
    }

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
        if (_eyes.Count > 0) UpdateEyes(dt);
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
            case State.Transit:
                _target = _transitTo;
                if (pos.DistanceTo(_transitTo) < 10f) Leaving = true;
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

        _fleeT -= dt; _stunT -= dt;
        if (_fleeT > 0f)
        {
            Vector3 away = pos - _fleeFrom; away.Y = 0;
            _target = pos + away.Normalized() * 80f + Vector3.Up * 3f;
            targetSpeed = Math.Max(Profile.PassSpeed, Profile.CruiseSpeed * 2.5f);
        }
        if (_stunT > 0f) targetSpeed = Profile.CruiseSpeed * 0.2f;
        if (SpeedOverride is float so) targetSpeed = so;

        // Steering: limited turn rate, limited pitch, keep clear of the seabed.
        Vector3 desired = (_target - pos).Normalized();
        if (Gen != null && !IgnoreSeabed)
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
        _speed = Mathf.MoveToward(_speed, targetSpeed, (_fleeT > 0f ? 3f : 1.0f) * dt);
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
            // Closed at rest (models are sculpted open-mouthed); opens only on the strike: the last
            // stretch of a pass, close to its target.
            bool attacking = _state == State.Pass && distToFocus < Profile.Length * 1.2f;
            float open = attacking ? Profile.JawOpen : Profile.JawClosed + 0.02f * MathF.Sin(_t * 0.7f);
            _jaw = Mathf.Lerp(_jaw, open, (attacking ? 3f : 1.5f) * dt);
            if (JawOverride is float jo) _jaw = jo;
            rot[Profile.Jaw] = Get(Profile.Jaw) * new Quaternion(jb.right, _jaw);
        }
        foreach (var (bone, q) in rot)
        {
            var bi = _bones[bone];
            _skel.SetBonePoseRotation(bi.idx, bi.rest * q);
        }
    }
}
