using System;
using System.Collections.Generic;
using Godot;

namespace Solitude.Creatures;

/// <summary>
/// The Abyssal Crawler (Tom's model, assets/Abyssal_Crawler.glb): a second station species beside
/// Sage's procedural crawler, which stays. Same AI (Crawler.cs); this is only its body and how it moves.
///
/// Rig: a body bone and eight two-segment legs (Leg.L / Leg.L.001..003, Leg.R..., each with a Leg1
/// child). Animated procedurally on the bones:
///   walk    — an alternating eight-leg gait: L0 R1 L2 R3 swing while R0 L1 R2 L3 hold, then swap;
///             each leg yaws about the body's up axis and lifts about (leg direction × up)
///   idle    — slow leg twitches, the body breathing
///   attack  — rears up, front legs raised
///   dead    — legs curl under, the body sinks, the glow fades
/// </summary>
public partial class Crawler
{
    /// <summary>Set before adding to the tree: Tom's abyssal crawler instead of the procedural one.</summary>
    public bool Abyssal;

    private const string AbyssalScene = "res://assets/Abyssal_Crawler.glb";
    private const float AbyssalScale = 1.45f;              // the model is ~0.95 m across; this makes it ~1.4 m
    private static readonly Color BaseTint = new(1.35f, 1.3f, 1.35f);   // brightens Tom's dark texture a little
    private Skeleton3D _skel;
    private Node3D _abyssRoot;
    private int _bodyBone = -1;
    private Quaternion _bodyRest;
    private Vector3 _bodyRestPos, _bodyPitchAxis;
    private readonly List<(int hip, int knee, Quaternion hipRest, Quaternion kneeRest, Vector3 yawAxis, Vector3 liftAxis, Vector3 kneeAxis, float phase, bool front)> _aLegs = new();

    private void BuildAbyssal()
    {
        _abyssRoot = new Node3D { Name = "Abyssal" };
        AddChild(_abyssRoot);
        var model = GD.Load<PackedScene>(AbyssalScene).Instantiate<Node3D>();
        // The model's head is toward +Z; Godot forward is −Z.
        var pivot = new Node3D { RotationDegrees = new Vector3(0, 180, 0), Position = new Vector3(0, 0.27f * AbyssalScale, 0) };
        _abyssRoot.AddChild(pivot);
        pivot.AddChild(model);
        model.Scale = Vector3.One * AbyssalScale;

        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/creature.gdshader") };
        foreach (var mi in Find<MeshInstance3D>(model))
        {
            if (mi.Mesh?.SurfaceGetMaterial(0) is BaseMaterial3D src)
            {
                _mat.SetShaderParameter("albedo_tex", src.AlbedoTexture);
                _mat.SetShaderParameter("mask_tex", src.AlbedoTexture);
                if (src.EmissionTexture != null)
                {
                    _mat.SetShaderParameter("use_emission_tex", true);
                    _mat.SetShaderParameter("emission_tex", src.EmissionTexture);
                    _mat.SetShaderParameter("emission_tex_energy", 2.2f);
                }
            }
            mi.MaterialOverride = _mat;
        }
        _mat.SetShaderParameter("eye_energy", 0f);
        // Tom's texture is dark purple; the creature shader's wet sheen (made for the big swimmers)
        // turned it into glossy blue smears under the stations' white lights. Matte, a little
        // brighter, and a softer glow so the eyes are eyes rather than blobs.
        _mat.SetShaderParameter("wet_spec", 0.12f);
        _mat.SetShaderParameter("roughness_v", 0.85f);
        _mat.SetShaderParameter("emission_tex_energy", 2.2f);
        _mat.SetShaderParameter("tint", BaseTint);

        foreach (var s in Find<Skeleton3D>(model)) { _skel = s; break; }
        if (_skel == null) return;
        Vector3 up = Vector3.Up;
        for (int i = 0; i < _skel.GetBoneCount(); i++)
        {
            string name = _skel.GetBoneName(i);
            if (name == "Bone")
            {
                _bodyBone = i;
                _bodyRest = _skel.GetBoneRest(i).Basis.GetRotationQuaternion();
                _bodyRestPos = _skel.GetBoneRest(i).Origin;
                _bodyPitchAxis = (_skel.GetBoneGlobalRest(i).Basis.Orthonormalized().Inverse() * Vector3.Right).Normalized();
                continue;
            }
            if (!name.StartsWith("Leg.")) continue;
            int knee = _skel.FindBone(name.Replace("Leg.", "Leg1."));
            if (knee < 0) continue;
            Basis g = _skel.GetBoneGlobalRest(i).Basis.Orthonormalized();
            Vector3 hipPos = _skel.GetBoneGlobalRest(i).Origin, kneePos = _skel.GetBoneGlobalRest(knee).Origin;
            Vector3 dir = (kneePos - hipPos).Normalized();
            Vector3 lift = dir.Cross(up);
            if (lift.LengthSquared() < 1e-4f) lift = Vector3.Right;
            lift = lift.Normalized();
            Basis gk = _skel.GetBoneGlobalRest(knee).Basis.Orthonormalized();
            // Gait groups: index 0..3 along the body (Leg / .001 / .002 / .003), alternating sides.
            int idx = name.EndsWith(".001") ? 1 : name.EndsWith(".002") ? 2 : name.EndsWith(".003") ? 3 : 0;
            bool left = name.Contains(".L");
            bool groupA = (idx % 2 == 0) == left;
            _aLegs.Add((i, knee, _skel.GetBoneRest(i).Basis.GetRotationQuaternion(), _skel.GetBoneRest(knee).Basis.GetRotationQuaternion(),
                (g.Inverse() * up).Normalized(), (g.Inverse() * lift).Normalized(), (gk.Inverse() * lift).Normalized(),
                groupA ? 0f : Mathf.Pi, kneePos.Z > 0.15f /* the head is toward +Z in the model */));
        }
    }

    private static IEnumerable<T> Find<T>(Node n) where T : Node
    {
        if (n is T t) yield return t;
        foreach (var c in n.GetChildren()) foreach (var r in Find<T>(c)) yield return r;
    }

    private void AnimateAbyssal(float dt, float speed)
    {
        if (_skel == null) return;
        _hurtFlash = Mathf.Max(0f, _hurtFlash - dt);
        _mat.SetShaderParameter("tint", _hurtFlash > 0f ? new Color(2.4f, 0.9f, 0.8f) : BaseTint);

        if (Current == State.Dead)
        {
            // Legs fold under, the body settles onto them, the light goes out of it.
            foreach (var l in _aLegs)
            {
                var cur = _skel.GetBonePoseRotation(l.hip);
                _skel.SetBonePoseRotation(l.hip, cur.Slerp(l.hipRest * new Quaternion(l.liftAxis, -0.9f), Mathf.Clamp(2f * dt, 0f, 1f)));
                var ck = _skel.GetBonePoseRotation(l.knee);
                _skel.SetBonePoseRotation(l.knee, ck.Slerp(l.kneeRest * new Quaternion(l.kneeAxis, -1.3f), Mathf.Clamp(2f * dt, 0f, 1f)));
            }
            _abyssRoot.Position = _abyssRoot.Position.Lerp(new Vector3(0, -0.22f, 0), Mathf.Clamp(2f * dt, 0f, 1f));
            _abyssRoot.Rotation = _abyssRoot.Rotation.Lerp(new Vector3(0, 0, 0.35f), Mathf.Clamp(1.5f * dt, 0f, 1f));
            _mat.SetShaderParameter("emission_tex_energy", Mathf.MoveToward((float)_mat.GetShaderParameter("emission_tex_energy"), 0f, dt * 2f));
            return;
        }

        bool moving = speed > 0.2f;
        float rate = 2f + speed * 3.6f;
        _gait += dt * rate * (moving ? 1f : 0.12f);
        float stride = 0.35f * Mathf.Clamp(speed / 2.5f, 0.12f, 1f);
        float lift = moving ? 0.45f : 0.06f;
        bool attack = Current == State.Attack;
        foreach (var l in _aLegs)
        {
            float s = Mathf.Sin(_gait + l.phase), c = Mathf.Max(0f, Mathf.Cos(_gait + l.phase));
            float yaw = s * stride;
            float up = c * lift;
            if (attack && l.front) up = 0.9f + Mathf.Sin(_t * 14f) * 0.15f;       // front legs raised to strike
            if (!moving && !attack) up += Mathf.Max(0f, Mathf.Sin(_t * 0.9f + l.phase * 3f)) * 0.08f;   // twitching while it waits
            _skel.SetBonePoseRotation(l.hip, l.hipRest * new Quaternion(l.yawAxis, yaw) * new Quaternion(l.liftAxis, up));
            _skel.SetBonePoseRotation(l.knee, l.kneeRest * new Quaternion(l.kneeAxis, -up * 0.7f));
        }
        if (_bodyBone >= 0)
        {
            float bob = Mathf.Sin(_gait * 2f) * 0.015f * Mathf.Clamp(speed, 0f, 1f) + Mathf.Sin(_t * 1.3f) * 0.008f;
            float rear = attack ? 0.45f : Current == State.Dormant ? -0.08f : 0f;
            _skel.SetBonePosePosition(_bodyBone, _bodyRestPos + Vector3.Up * bob);
            var target = _bodyRest * new Quaternion(_bodyPitchAxis, rear);
            _skel.SetBonePoseRotation(_bodyBone, _skel.GetBonePoseRotation(_bodyBone).Slerp(target, Mathf.Clamp(6f * dt, 0f, 1f)));
        }
    }
}
