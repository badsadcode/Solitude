using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.Sub;
using Solitude.World;

namespace Solitude.Creatures;

/// <summary>
/// Tom's ravager (assets/ravager.glb), brought to life procedurally — no keyframes needed.
///
/// Skeleton (from the Blender export; the model faces +Z, ~0.96 m long):
///   Bone      torso / pectoral fins (root)
///   Bone.001  neck        Bone.005 skull        Bone.006 lower jaw
///   Bone.002 → Bone.003 → Bone.004  tail chain ending in a vertical fin
///
/// Animation: a travelling sine wave down the tail chain (frequency tied to speed), the
/// body curls into turns, the head counter-sways, the jaw breathes and gapes on close passes.
/// Behaviour (test build): lurks 60–90 m out, circling; periodically crosses in front of
/// the bow through the floodlights. ForcePass() (F9) summons a crossing.
/// </summary>
public partial class Ravager : Node3D
{
    public Submarine Sub;
    public TerrainGenerator Gen;
    public AudioDirector Audio;

    [Export] public float Length = 15f;
    private const float ModelLength = 0.96f;

    private enum State { Lurk, Approach, Pass }
    public const float JawClosedAngle = -0.65f, JawOpenAngle = 0.35f;
    /// <summary>Debug/capture: force the jaw angle.</summary>
    public float? JawOverride;
    private State _state = State.Lurk;

    private Skeleton3D _skel;
    private ShaderMaterial _mat;
    private readonly Dictionary<string, int> _bones = new();
    private readonly Dictionary<int, (Quaternion rest, Vector3 yawAxis, Vector3 pitchAxis)> _rig = new();

    private Vector3 _fwd = Vector3.Forward;
    private float _speed = 3.5f;
    private float _phase, _yawRate, _bank, _jaw, _t;
    private float _orbitAngle, _orbitRadius = 75f, _orbitHeight;
    private float _passTimer = 25f;
    private Vector3 _passStart, _passCross, _passEnd;
    private readonly Random _rng = new();

    public bool Visible3D => _skel != null;

    public override void _Ready()
    {
        var scene = GD.Load<PackedScene>("res://assets/ravager.glb");
        var model = scene.Instantiate<Node3D>();
        // The model's front is +Z; Godot's forward is -Z. Turn it around inside a pivot.
        var pivot = new Node3D { Name = "Pivot", RotationDegrees = new Vector3(0, 180, 0) };
        AddChild(pivot);
        pivot.AddChild(model);
        model.Scale = Vector3.One * (Length / ModelLength);

        _skel = FindFirst<Skeleton3D>(model);
        var tex = GD.Load<Texture2D>("res://assets/ravager_Image_0.png");
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/creature.gdshader") };
        _mat.SetShaderParameter("albedo_tex", tex);
        _mat.SetShaderParameter("mask_tex", tex);
        foreach (var mi in FindAll<MeshInstance3D>(model))
        {
            mi.MaterialOverride = _mat;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        }

        if (_skel != null)
        {
            for (int i = 0; i < _skel.GetBoneCount(); i++)
            {
                string key = Norm(_skel.GetBoneName(i));
                _bones[key] = i;
                Basis g = _skel.GetBoneGlobalRest(i).Basis.Orthonormalized();
                Basis inv = g.Inverse();
                _rig[i] = (_skel.GetBoneRest(i).Basis.GetRotationQuaternion(),
                           (inv * Vector3.Up).Normalized(),
                           (inv * Vector3.Right).Normalized());
            }
            GD.Print($"[Ravager] skeleton with {_skel.GetBoneCount()} bones: {string.Join(", ", _bones.Keys)}");
        }
        else GD.PushWarning("[Ravager] no Skeleton3D found — swimming without bones");

        _orbitAngle = (float)_rng.NextDouble() * Mathf.Tau;
        if (Sub != null) GlobalPosition = Sub.GlobalPosition + new Vector3(MathF.Sin(_orbitAngle), 0, MathF.Cos(_orbitAngle)) * _orbitRadius;
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static T FindFirst<T>(Node n) where T : Node
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren()) { var r = FindFirst<T>(c); if (r != null) return r; }
        return null;
    }

    private static IEnumerable<T> FindAll<T>(Node n) where T : Node
    {
        if (n is T t) yield return t;
        foreach (var c in n.GetChildren()) foreach (var r in FindAll<T>(c)) yield return r;
    }

    // ------------------------------------------------------------------ behaviour

    /// <summary>Cross in front of the bow. startLateral = how far to the side it starts (m).</summary>
    public void ForcePass(float startLateral = 70f, bool teleport = false)
    {
        if (Sub == null) return;
        Basis sb = Sub.GlobalBasis;
        Vector3 f = -sb.Z; f.Y = 0; f = f.Normalized();
        Vector3 r = new(-f.Z, 0, f.X);
        float side = _rng.NextDouble() < 0.5 ? -1f : 1f;
        float y = (float)(_rng.NextDouble() * 6 - 2);
        _passCross = Sub.GlobalPosition + f * 30f + Vector3.Up * y;
        _passStart = _passCross - r * side * startLateral + f * 12f;
        _passEnd = _passCross + r * side * 80f - f * 6f;
        _state = State.Approach;
        if (teleport)
        {
            GlobalPosition = _passStart;
            _fwd = (_passCross - _passStart).Normalized();
            _state = State.Pass;
        }
        Audio?.PlayCall();
    }

    // ------------------------------------------------------------------ divers

    /// <summary>Set while the captain is outside in the suit. The ravager prefers divers.</summary>
    public Node3D Diver;
    public Func<float> DiverTimeOutside;
    public event Action<float> Bite;            // damage
    public float DistanceToDiver => Diver != null ? GlobalPosition.DistanceTo(Diver.GlobalPosition) : float.MaxValue;
    private bool _hadDiver;
    private float _biteCooldown;

    /// <summary>A slow pass close by the diver: the first ones only look; later ones may bite.</summary>
    public void DiverPass()
    {
        Vector3 d = Diver.GlobalPosition;
        float a = (float)_rng.NextDouble() * Mathf.Tau;
        Vector3 across = new(MathF.Cos(a), 0, MathF.Sin(a));
        Vector3 side = new(-across.Z, 0, across.X);
        _passCross = d + side * (7f + (float)_rng.NextDouble() * 4f) + Vector3.Up * (float)(_rng.NextDouble() * 4 - 1);
        _passStart = _passCross - across * 60f;
        _passEnd = _passCross + across * 70f;
        _state = State.Approach;
        Audio?.PlayCall();
    }

    public override void _Process(double delta)
    {
        float dt = (float)Math.Min(delta, 0.1);
        _t += dt;
        if (Sub == null) return;
        Vector3 pos = GlobalPosition;
        // It circles whatever it finds most interesting: a diver, otherwise the boat.
        bool diver = Diver != null;
        if (diver && !_hadDiver) _passTimer = Math.Min(_passTimer, 30f + (float)_rng.NextDouble() * 15f);
        _hadDiver = diver;
        Vector3 sp = diver ? Diver.GlobalPosition : Sub.GlobalPosition;

        // Leash: if it has fallen far behind whatever it follows, it quietly relocates into the
        // fog nearby (never inside visibility range: the fog hides the cheat).
        float leash = diver ? 160f : 320f;
        if (pos.DistanceTo(sp) > leash && _state == State.Lurk)
        {
            float a = (float)_rng.NextDouble() * Mathf.Tau;
            pos = sp + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * (diver ? 90f : 140f) + Vector3.Up * (float)(_rng.NextDouble() * 10 - 3);
            GlobalPosition = pos;
            _orbitAngle = a;
        }

        // Bites: only once the diver has overstayed, only during a pass, and only up close.
        _biteCooldown -= dt;
        if (diver && _state == State.Pass && _biteCooldown <= 0f && (DiverTimeOutside?.Invoke() ?? 0f) > 150f
            && pos.DistanceTo(sp) < Length * 0.35f)
        {
            _biteCooldown = 20f;
            Bite?.Invoke(35f);
        }

        // --- choose a target
        Vector3 target;
        float targetSpeed;
        switch (_state)
        {
            case State.Approach:
                target = _passStart; targetSpeed = 6f;
                if (pos.DistanceTo(_passStart) < 12f) _state = State.Pass;
                break;
            case State.Pass:
                target = pos.DistanceTo(_passCross) > 10f && (_passCross - pos).Dot(_passEnd - _passCross) > 0 ? _passCross : _passEnd;
                targetSpeed = 6.5f;
                if (pos.DistanceTo(_passEnd) < 15f) { _state = State.Lurk; _passTimer = diver ? 35f + (float)_rng.NextDouble() * 30f : 60f + (float)_rng.NextDouble() * 60f; }
                break;
            default:
                _passTimer -= dt;
                if (_passTimer <= 0f) { if (diver) DiverPass(); else ForcePass(); }
                _orbitAngle += dt * 3.5f / _orbitRadius;
                _orbitRadius = 75f + MathF.Sin(_t * 0.05f) * 15f;
                _orbitHeight = MathF.Sin(_t * 0.07f) * 10f - 4f;
                target = sp + new Vector3(MathF.Sin(_orbitAngle), 0, MathF.Cos(_orbitAngle)) * _orbitRadius + Vector3.Up * _orbitHeight;
                targetSpeed = 3.5f;
                break;
        }

        // --- steering: turn-rate limited, pitch limited, keep off the seabed
        Vector3 desired = (target - pos).Normalized();
        if (Gen != null)
        {
            Vector3 ahead = pos + _fwd * 20f;
            float floor = Gen.Height(ahead.X, ahead.Z) + Length * 0.5f;
            if (ahead.Y < floor) desired = (desired + Vector3.Up * 1.2f).Normalized();
        }
        Vector3 oldFlat = new Vector3(_fwd.X, 0, _fwd.Z).Normalized();
        float angle = _fwd.AngleTo(desired);
        const float maxTurn = 0.45f; // rad/s
        if (angle > 1e-4f) _fwd = _fwd.Slerp(desired, Math.Min(1f, maxTurn * dt / angle)).Normalized();
        _fwd.Y = Math.Clamp(_fwd.Y, -0.4f, 0.4f);
        _fwd = _fwd.Normalized();
        Vector3 newFlat = new Vector3(_fwd.X, 0, _fwd.Z).Normalized();
        float signedYaw = oldFlat.SignedAngleTo(newFlat, Vector3.Up);
        _yawRate = Mathf.Lerp(_yawRate, signedYaw / Math.Max(dt, 1e-4f), 3f * dt);

        _speed = Mathf.MoveToward(_speed, targetSpeed, 1.2f * dt);
        pos += _fwd * _speed * dt;
        if (Gen != null) pos.Y = Math.Max(pos.Y, Gen.Height(pos.X, pos.Z) + Length * 0.25f);

        _bank = Mathf.Lerp(_bank, Math.Clamp(-_yawRate * 0.8f, -0.5f, 0.5f), 2f * dt);
        GlobalTransform = new Transform3D(Basis.LookingAt(_fwd, Vector3.Up) * new Basis(Vector3.Forward, _bank), pos);

        Animate(dt, pos.DistanceTo(sp));
    }

    // ------------------------------------------------------------------ animation

    private void Animate(float dt, float distToSub)
    {
        if (_skel == null) return;
        float freq = 0.25f + _speed * 0.09f;            // tail beats per second
        _phase += dt * freq * Mathf.Tau;
        float curl = Math.Clamp(_yawRate * 0.9f, -0.5f, 0.5f);

        // Jaw (Tom: "close them, open only when they attack"). The model's rest pose is sculpted open,
        // so "closed" is a negative offset. It opens only during a pass that comes close — the strike.
        bool attacking = _state == State.Pass && distToSub < 32f;
        float jawTarget = attacking ? JawOpenAngle * (0.85f + 0.15f * MathF.Sin(_t * 3f)) : JawClosedAngle + 0.02f * MathF.Sin(_t * 0.8f);
        _jaw = Mathf.Lerp(_jaw, jawTarget, (attacking ? 4f : 2f) * dt);
        if (JawOverride is float jo) _jaw = jo;

        // Curl into turns: positive yaw swings forward-pointing bones (head) to the creature's left and
        // backward-pointing bones (tail) to its right, so both take +curl on a left turn.
        Yaw("bone", 0.05f * MathF.Sin(_phase + 1.2f) + curl * 0.15f);
        Yaw("bone001", -0.06f * MathF.Sin(_phase + 1.8f) + curl * 0.25f);
        Yaw("bone005", -0.05f * MathF.Sin(_phase + 2.2f) + curl * 0.15f, 0.03f * MathF.Sin(_t * 0.5f));
        Pitch("bone006", _jaw);
        Yaw("bone002", 0.16f * MathF.Sin(_phase) + curl * 0.35f);
        Yaw("bone003", 0.24f * MathF.Sin(_phase - 0.9f) + curl * 0.4f);
        Yaw("bone004", 0.34f * MathF.Sin(_phase - 1.8f) + curl * 0.45f);

        // Eyes blink very rarely, and dim when it is in the floodlights (it doesn't like being seen).
        float blink = Mathf.PosMod(_t, 11.3f) < 0.15f ? 0f : 1f;
        _mat.SetShaderParameter("eye_open", blink);
    }

    private void Yaw(string bone, float angle, float pitch = 0f)
    {
        if (!_bones.TryGetValue(bone, out int i)) return;
        var (rest, yawAxis, pitchAxis) = _rig[i];
        var q = rest * new Quaternion(yawAxis, angle);
        if (pitch != 0f) q *= new Quaternion(pitchAxis, pitch);
        _skel.SetBonePoseRotation(i, q);
    }

    private void Pitch(string bone, float angle)
    {
        if (!_bones.TryGetValue(bone, out int i)) return;
        var (rest, _, pitchAxis) = _rig[i];
        _skel.SetBonePoseRotation(i, rest * new Quaternion(pitchAxis, angle));
    }
}
