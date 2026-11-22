using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Systems;
using Solitude.World;

namespace Solitude.Player;

/// <summary>
/// The captain in the hard suit, outside the boat. A separate body from the interior player
/// (the interior lives far above the ocean); shares the player's health.
///
/// Swimming: WASD relative to where you look (full 3D), Space / C(trl) straight up / down,
/// Shift boost. Water drag, a little inertia, slightly negative buoyancy, a faint current.
/// Oxygen: ~4 minutes of air, boost breathes harder. Helmet lamp. A six-slot sample bag
/// for things picked up by hand, emptied into the hold back aboard.
/// </summary>
public partial class Diver : CharacterBody3D
{
    public PlayerController Player;      // health lives there
    public TerrainStreamer Terrain;

    public Camera3D Camera { get; private set; }
    public SpotLight3D Lamp { get; private set; }
    public float Oxygen { get; private set; } = 100f;
    public float Exertion { get; private set; }   // 0..1, drives breathing rate
    public bool Active { get; private set; }
    public float TimeOutside { get; private set; }
    public readonly List<ItemId> Bag = new();
    /// <summary>Id of the last pickup collected (wreck salvage is recognised by it).</summary>
    public long LastId;
    public const int BagSize = 6;
    public TerrainStreamer.Pickup Nearest { get; private set; }

    public event Action OxygenWarning;

    private Node3D _yaw;
    private float _pitch, _drownTick, _warn;
    private Vector3 _current;
    /// <summary>Water velocity at a point (set by the director); null = the old gentle wander.</summary>
    public System.Func<Vector3, Vector3> WaterAt;
    private float _t;

    // Tuned by Tom's first swim (2026-11-22): "a bit slower" — ~15% off, slightly heavier starts.
    private const float SwimSpeed = 1.6f, BoostSpeed = 2.6f, Accel = 1.35f, Drag = 1.4f;
    /// <summary>Minutes of air in the tank (the Suit tank module raises it).</summary>
    public float TankMinutes = 4f;
    /// <summary>Rebreather module: how much harder you breathe when working (1 = stock).</summary>
    public float ExertionAir = 1f;
    private float O2PerSecond => 100f / (TankMinutes * 60f);

    public override void _Ready()
    {
        CollisionLayer = Layers.Player;
        CollisionMask = Layers.World | Layers.Sub;
        MotionMode = MotionModeEnum.Floating;
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        _yaw = new Node3D { Name = "Yaw", Position = new Vector3(0, 0.6f, 0) };
        AddChild(_yaw);
        Camera = new Camera3D { Name = "Helmet", Fov = 74f, Near = 0.08f, Far = 220f };
        _yaw.AddChild(Camera);
        Lamp = new SpotLight3D
        {
            Position = new Vector3(0.12f, 0.12f, 0f), SpotRange = 30f, SpotAngle = 24f, SpotAngleAttenuation = 1.3f,
            LightEnergy = 5f, LightColor = new Color(0.95f, 0.95f, 1f), LightVolumetricFogEnergy = 1.4f, ShadowEnabled = true,
        };
        Camera.AddChild(Lamp);
        SetActive(false);
    }

    public void SetActive(bool on)
    {
        Active = on;
        Visible = on;
        SetPhysicsProcess(on);
        CollisionLayer = on ? Layers.Player : 0;
        if (on) { Oxygen = 100f; TimeOutside = 0f; Velocity = Vector3.Zero; _warn = 0f; }
    }

    public void Place(Vector3 pos, float yaw)
    {
        GlobalPosition = pos;
        _yaw.Rotation = new Vector3(0, yaw, 0);
        _pitch = -0.3f;
        Camera.Rotation = new Vector3(_pitch, 0, 0);
    }

    public void Look(Vector2 rel)
    {
        if (!Active) return;
        _yaw.RotateY(-rel.X * 0.0022f);
        _pitch = Mathf.Clamp(_pitch - rel.Y * 0.0022f, -1.5f, 1.5f);
        Camera.Rotation = new Vector3(_pitch, 0, 0);
    }

    /// <summary>Put the nearest reachable pickup in the bag. Returns its type or null.</summary>
    public ItemId? Collect()
    {
        if (Nearest == null || Bag.Count >= BagSize) return null;
        var pk = Nearest;
        LastId = pk.Id;
        Terrain.Take(pk);
        Bag.Add(pk.Type);
        Nearest = null;
        return pk.Type;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        TimeOutside += dt;

        Vector3 wish = Vector3.Zero;
        bool boost = false;
        if (Player != null && !Player.Dead)
        {
            Basis look = Camera.GlobalBasis;
            Vector2 input = Input.GetVector(GameInput.Left, GameInput.Right, GameInput.Forward, GameInput.Back);
            // input.Y is -1 for "forward" (W); forward is where the helmet looks, including up/down.
            wish = look.X * input.X + (-look.Z) * (-input.Y);
            if (Input.IsActionPressed(GameInput.Jump)) wish += Vector3.Up;
            if (Input.IsActionPressed(GameInput.BallastFlood)) wish += Vector3.Down;
            if (wish.LengthSquared() > 1f) wish = wish.Normalized();
            boost = Input.IsActionPressed(GameInput.Sprint) && wish.LengthSquared() > 0.01f;
        }

        // The water moves (Systems/Currents.cs): you swim relative to it, so in still water this is
        // exactly the old feel, and in a current you're carried. Slight negative buoyancy on top.
        _current = WaterAt?.Invoke(GlobalPosition) ?? new Vector3(Mathf.Sin(_t * 0.05f), 0, Mathf.Cos(_t * 0.037f)) * 0.08f;
        Vector3 rel = Velocity - _current;
        float max = boost ? BoostSpeed : SwimSpeed;
        rel += (wish * max - rel) * Mathf.Clamp(Accel * dt, 0f, 1f);
        rel -= rel * Mathf.Clamp(Drag * 0.2f * dt, 0f, 1f);
        Vector3 v = rel + _current + Vector3.Down * 0.06f * dt;
        Velocity = v;
        MoveAndSlide();

        // Air
        float effort = wish.Length() * (boost ? 1f : 0.4f);
        Exertion = Mathf.Lerp(Exertion, effort, Mathf.Clamp(0.8f * dt, 0f, 1f));
        Oxygen = Mathf.Max(0f, Oxygen - O2PerSecond * (1f + Exertion * 1.2f * ExertionAir) * dt);
        if (Oxygen < 25f && _warn <= 0f) { OxygenWarning?.Invoke(); _warn = 12f; }
        _warn -= dt;
        if (Oxygen <= 0f)
        {
            _drownTick -= dt;
            if (_drownTick <= 0f) { Player?.Damage(8f); _drownTick = 1.2f; }
        }

        // What can I reach?
        if (Terrain != null && Bag.Count < BagSize)
        {
            var pk = Terrain.NearestPickup(Camera.GlobalPosition, 3.2f);
            Nearest = pk != null && pk.Position.DistanceTo(Camera.GlobalPosition) < 2.6f ? pk : null;
        }
        else Nearest = null;
    }
}
