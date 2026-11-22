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
    /// <summary>The gentle giant, while one is passing.</summary>
    public Swimmer Whale { get; private set; }
    private float _whaleT = 240f, _songT = 8f;

    private float _rayCheck = 3f, _deepTime, _wyrmCooldown;
    private readonly Random _rng = new();

    public IEnumerable<Vector3> Bodies()
    {
        foreach (var r in Rays) yield return r.GlobalPosition;
        if (Whale != null)
            // The biggest thing on the scope is a chain of blips, not one: a ridge that moves.
            for (int i = -4; i <= 4; i++) yield return Whale.GlobalPosition + Whale.GlobalBasis.Z * i * 8f;
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

    /// <summary>
    /// The gentle giant passes: from far out on one side to far out on the other, at a distance from
    /// the focus (sometimes right overhead). It never attacks, never stops, never doubles back.
    /// </summary>
    /// <summary>Gone (dev / capture).</summary>
    public void DismissWhale() { Whale?.QueueFree(); Whale = null; }

    public void SummonWhale(bool overhead = false)
    {
        if (Whale != null) return;
        Vector3 f = Focus();
        float a = (float)_rng.NextDouble() * Mathf.Tau;
        Vector3 dir = new(MathF.Sin(a), 0, MathF.Cos(a));
        Vector3 side = new(-dir.Z, 0, dir.X);
        // Sized for a 70 m whale ~20 m tall: alongside it keeps its distance; overhead its belly clears the sail.
        float lateral = overhead ? 0f : 70f + (float)_rng.NextDouble() * 60f;
        float rise = overhead ? 42f + (float)_rng.NextDouble() * 10f : (float)_rng.NextDouble() * 24f - 4f;
        Vector3 cross = f + side * lateral + Vector3.Up * rise;
        Vector3 start = cross - dir * 320f, end = cross + dir * 360f;
        float floorS = Gen?.Height(start.X, start.Z) ?? start.Y;
        start.Y = Math.Max(start.Y, floorS + 35f);
        Whale = Spawn(CreatureProfile.Whale(), start, f);
        Whale.Transit(end);
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
        // --- the whale: rare, in deep water, never while the wyrm is about
        _whaleT -= dt;
        if (Whale == null && Wyrm == null && _whaleT <= 0f)
        {
            _whaleT = 420f + (float)_rng.NextDouble() * 480f;   // 7-15 minutes between chances
            if (depth > 300f && _rng.NextDouble() < 0.6) SummonWhale(_rng.NextDouble() < 0.3);
        }
        if (Whale != null)
        {
            // It sings while it passes: long, low, gliding calls through the hull.
            _songT -= dt;
            float wd = Whale.DistanceToFocus;
            if (_songT <= 0f && wd < 450f)
            {
                _songT = 14f + (float)_rng.NextDouble() * 18f;
                Vector3 rel = Whale.GlobalPosition - f;
                Audio?.PlayAt("whalesong", Mathf.Clamp(rel.X / Mathf.Max(1f, rel.Length()), -0.8f, 0.8f), Mathf.Clamp(1f - wd / 450f, 0.15f, 1f));
            }
            if (Whale.Leaving || wd > 700f) { Whale.QueueFree(); Whale = null; _songT = 6f; }
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
