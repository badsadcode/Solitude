using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.World;

namespace Solitude.Creatures;

/// <summary>
/// Decides which of Tom's creatures are around (the ravager lives on its own):
///   - Crystal Rays: small groups over glowing meadows (rarely elsewhere), harmless and curious.
///   - The Fangwyrm: only after you've spent time below ~520 m; circles far out, makes a few long
///     passes, then leaves. One at a time. Never explained.
/// Spawns and despawns happen out in the fog, never in view.
/// </summary>
public partial class CreatureDirector : Node3D
{
    public TerrainGenerator Gen;
    public AudioDirector Audio;
    public Func<Vector3> Focus;
    public Func<bool> FocusIsDiver;
    public Func<float> DiverTimeOutside;
    public Func<bool> LightsOn;

    public event Action<float> HullBrush;
    public event Action<float> BiteDiver;

    public const float DeepThreshold = 520f;

    public readonly List<Swimmer> Rays = new();
    public Swimmer Wyrm { get; private set; }

    private float _rayCheck = 3f, _deepTime, _wyrmCooldown;
    private readonly Random _rng = new();

    public IEnumerable<Vector3> Bodies()
    {
        foreach (var r in Rays) yield return r.GlobalPosition;
        if (Wyrm != null)
        {
            // A long body: several returns along the spine.
            for (int i = -2; i <= 2; i++) yield return Wyrm.GlobalPosition + Wyrm.GlobalBasis.Z * i * 8f;
        }
    }

    /// <summary>Distance from the focus to the nearest apex creature (for panic / heartbeat).</summary>
    public float ApexDistance => Wyrm?.DistanceToFocus ?? float.MaxValue;

    private Swimmer Spawn(CreatureProfile p, Vector3 at, Vector3 anchor)
    {
        var s = new Swimmer
        {
            Profile = p, Gen = Gen, Audio = Audio, Focus = Focus, FocusIsDiver = FocusIsDiver,
            DiverTimeOutside = DiverTimeOutside, LightsOn = LightsOn, Anchor = anchor, Name = p.Name.Replace(" ", ""),
        };
        AddChild(s);
        s.GlobalPosition = at;
        s.HullBrush += x => HullBrush?.Invoke(x);
        s.BiteDiver += x => BiteDiver?.Invoke(x);
        return s;
    }

    private Vector3 OffscreenPoint(Vector3 around, float dist, float lift)
    {
        float a = (float)_rng.NextDouble() * Mathf.Tau;
        var p = around + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * dist;
        float floor = Gen?.Height(p.X, p.Z) ?? p.Y;
        p.Y = Math.Max(around.Y + (float)(_rng.NextDouble() * 10 - 5), floor + lift);
        return p;
    }

    // ------------------------------------------------------------------ debug summons

    public void SummonRays()
    {
        Vector3 f = Focus();
        Vector3 at = OffscreenPoint(f, 40f, 6f);
        for (int i = 0; i < 3; i++) Rays.Add(Spawn(CreatureProfile.CrystalRay(), at + new Vector3(i * 6f, i * 2f, -i * 5f), f));
    }

    public void SummonWyrm()
    {
        Wyrm ??= Spawn(CreatureProfile.Fangwyrm(), OffscreenPoint(Focus(), 200f, 20f), Focus());
        Wyrm.ForcePass(teleport: true);
    }

    // ------------------------------------------------------------------ director

    public override void _Process(double delta)
    {
        if (Focus == null) return;
        float dt = (float)delta;
        Vector3 f = Focus();

        // --- rays
        _rayCheck -= dt;
        if (_rayCheck <= 0f)
        {
            _rayCheck = 6f;
            for (int i = Rays.Count - 1; i >= 0; i--)
                if (Rays[i].DistanceToFocus > 320f) { Rays[i].QueueFree(); Rays.RemoveAt(i); }
            float meadow = Gen?.FloraDensity(f.X, f.Z) ?? 0f;
            double chance = meadow > 0.25f ? 0.5 : 0.06;
            if (Rays.Count == 0 && _rng.NextDouble() < chance)
            {
                Vector3 at = OffscreenPoint(f, 100f + (float)_rng.NextDouble() * 40f, 6f);
                int n = 1 + _rng.Next(3);
                for (int i = 0; i < n; i++)
                    Rays.Add(Spawn(CreatureProfile.CrystalRay(), at + new Vector3((float)_rng.NextDouble() * 12f, (float)_rng.NextDouble() * 4f, (float)_rng.NextDouble() * 12f), at));
            }
        }

        // --- the wyrm: earned by lingering in the deep
        float depth = -f.Y;
        _deepTime = depth > DeepThreshold ? _deepTime + dt : Mathf.Max(0f, _deepTime - dt * 0.5f);
        _wyrmCooldown -= dt;
        if (Wyrm == null && _deepTime > 45f && _wyrmCooldown <= 0f && _rng.NextDouble() < dt / 30f)
        {
            Wyrm = Spawn(CreatureProfile.Fangwyrm(), OffscreenPoint(f, 260f, 25f), f);
            _deepTime = 0f;
        }
        if (Wyrm != null)
        {
            if (!Wyrm.Leaving && (depth < DeepThreshold - 60f || _rng.NextDouble() < dt / 400f)) Wyrm.Leave();
            if (Wyrm.Leaving && Wyrm.DistanceToFocus > 450f)
            {
                Wyrm.QueueFree();
                Wyrm = null;
                _wyrmCooldown = 240f;
            }
        }
    }
}
