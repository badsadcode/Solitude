using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;

namespace Solitude.Player;

public enum WeaponId { Pistol, Rifle, Shotgun }

/// <summary>Anything a bullet can hurt.</summary>
public interface IDamageable
{
    void TakeDamage(float amount, Vector3 point, Vector3 direction);
}

/// <summary>
/// The captain's sidearms. Hitscan, PS1-chunky viewmodels built from boxes, magazine + reserve
/// ammo, reloads, recoil kick, mouse sway, muzzle flash light and impact sparks. Lives under the
/// player camera; only active aboard stations. Every shot raises a noise event (creatures hear it).
/// </summary>
public partial class Weapons : Node3D
{
    public sealed record Def(string Name, float Damage, int Pellets, float Spread, float Interval, int Mag, bool Auto, float Range, float Kick, float Reload);

    public static readonly Dictionary<WeaponId, Def> Defs = new()
    {
        [WeaponId.Pistol] = new("PISTOL", 24f, 1, 0.012f, 0.22f, 12, false, 60f, 0.05f, 1.2f),
        [WeaponId.Rifle] = new("ASSAULT RIFLE", 15f, 1, 0.028f, 0.095f, 30, true, 70f, 0.03f, 1.9f),
        [WeaponId.Shotgun] = new("SHOTGUN", 10f, 9, 0.095f, 0.85f, 6, false, 30f, 0.13f, 2.3f),
    };

    public event Action<Vector3, float> Noise;       // position, loudness radius (m)
    public event Action<WeaponId> Shot;
    public event Action<WeaponId> Reloaded;
    public event Action EmptyClick;

    public bool Active { get; private set; }
    public WeaponId Current { get; private set; } = WeaponId.Pistol;
    public readonly HashSet<WeaponId> Owned = new() { WeaponId.Pistol };
    public readonly Dictionary<WeaponId, int> Mag = new() { [WeaponId.Pistol] = 12, [WeaponId.Rifle] = 0, [WeaponId.Shotgun] = 0 };
    public readonly Dictionary<WeaponId, int> Reserve = new() { [WeaponId.Pistol] = 24, [WeaponId.Rifle] = 0, [WeaponId.Shotgun] = 0 };
    public bool Reloading => _reload > 0f;
    /// <summary>Horizontal speed of whoever carries the gun (for viewmodel bob).</summary>
    public Func<float> CarrierSpeed;

    private readonly Dictionary<WeaponId, Node3D> _models = new();
    private OmniLight3D _flash;
    private MeshInstance3D _flashQuad;
    private float _cool, _reload, _kick, _flashT, _bob, _switchT;
    private Vector2 _sway;
    private readonly Random _rng = new();
    private readonly List<(MeshInstance3D m, float t)> _sparks = new();
    private static ArrayMesh _sparkMesh;

    private static readonly Vector3 Hip = new(0.2f, -0.2f, -0.42f);

    public override void _Ready()
    {
        _models[WeaponId.Pistol] = Model(BuildPistol());
        _models[WeaponId.Rifle] = Model(BuildRifle());
        _models[WeaponId.Shotgun] = Model(BuildShotgun());
        _flash = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.4f), OmniRange = 9f, LightEnergy = 0f, Position = new Vector3(0.2f, -0.12f, -0.9f) };
        AddChild(_flash);
        var fq = new MeshBuilder();
        fq.Rock(Vector3.Zero, new Vector3(0.06f, 0.06f, 0.12f), new Color(1f, 0.8f, 0.4f, 0.3f), 1, 0.4f);
        _flashQuad = new MeshInstance3D { Mesh = fq.Build(Art.Machine), Visible = false };
        AddChild(_flashQuad);
        SetActive(false);
    }

    private Node3D Model(ArrayMesh mesh)
    {
        var n = new Node3D { Position = Hip };
        n.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(n);
        return n;
    }

    // ------------------------------------------------------------------ viewmodels (metres, -Z forward)

    private static readonly Color Gun = new(0.16f, 0.17f, 0.17f), GunLight = new(0.3f, 0.31f, 0.3f), Grip = new(0.22f, 0.15f, 0.1f);

    private static ArrayMesh BuildPistol()
    {
        var b = new MeshBuilder();
        b.Box(new Vector3(0, 0.03f, -0.08f), new Vector3(0.04f, 0.05f, 0.22f), Gun);
        b.Box(new Vector3(0, 0.065f, -0.08f), new Vector3(0.042f, 0.02f, 0.22f), GunLight);
        b.BoxOriented(new Transform3D(new Basis(Vector3.Right, 0.25f), new Vector3(0, -0.04f, 0.0f)), new Vector3(0.038f, 0.12f, 0.05f), Grip);
        b.Box(new Vector3(0, 0.08f, -0.18f), new Vector3(0.01f, 0.012f, 0.01f), new Color(0.9f, 0.3f, 0.2f, 0.5f));
        return b.Build(Art.Machine);
    }

    private static ArrayMesh BuildRifle()
    {
        var b = new MeshBuilder();
        b.Box(new Vector3(0, 0.03f, -0.12f), new Vector3(0.05f, 0.07f, 0.42f), Gun);
        b.Box(new Vector3(0, 0.03f, -0.42f), new Vector3(0.025f, 0.025f, 0.22f), GunLight);           // barrel
        b.BoxOriented(new Transform3D(new Basis(Vector3.Right, -0.2f), new Vector3(0, -0.06f, -0.15f)), new Vector3(0.035f, 0.13f, 0.06f), Gun.Shade(1.2f)); // mag
        b.BoxOriented(new Transform3D(new Basis(Vector3.Right, 0.3f), new Vector3(0, -0.05f, 0.02f)), new Vector3(0.035f, 0.1f, 0.045f), Grip);
        b.Box(new Vector3(0, 0.02f, 0.14f), new Vector3(0.04f, 0.06f, 0.16f), Grip);                  // stock
        b.Box(new Vector3(0, 0.085f, -0.12f), new Vector3(0.03f, 0.03f, 0.08f), GunLight);              // sight
        b.Box(new Vector3(0, 0.1f, -0.1f), new Vector3(0.008f, 0.008f, 0.008f), new Color(0.3f, 1f, 0.4f, 0.4f));
        return b.Build(Art.Machine);
    }

    private static ArrayMesh BuildShotgun()
    {
        var b = new MeshBuilder();
        b.Xform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), new Vector3(0, 0.045f, -0.08f));
        b.Cylinder(Vector3.Zero, 0.022f, 0.022f, 0.48f, 6, Gun, caps: true);
        b.Xform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), new Vector3(0, 0.005f, -0.08f));
        b.Cylinder(Vector3.Zero, 0.02f, 0.02f, 0.42f, 6, GunLight, caps: true);
        b.Xform = Transform3D.Identity;
        b.Box(new Vector3(0, 0.0f, -0.3f), new Vector3(0.05f, 0.04f, 0.12f), Grip);                   // pump
        b.Box(new Vector3(0, 0.02f, 0.05f), new Vector3(0.05f, 0.07f, 0.12f), Gun);                    // receiver
        b.BoxOriented(new Transform3D(new Basis(Vector3.Right, 0.35f), new Vector3(0, -0.04f, 0.14f)), new Vector3(0.045f, 0.1f, 0.2f), Grip);
        return b.Build(Art.Machine);
    }

    // ------------------------------------------------------------------ API

    public void SetActive(bool on)
    {
        Active = on;
        foreach (var kv in _models) kv.Value.Visible = on && kv.Key == Current;
        _reload = 0f;
    }

    public void Give(WeaponId id, int ammo)
    {
        bool fresh = Owned.Add(id);
        if (fresh) Mag[id] = Math.Min(Defs[id].Mag, ammo);
        else Reserve[id] += ammo;
        if (fresh) Select(id);
    }

    public void AddAmmo(WeaponId id, int n) => Reserve[id] += n;

    public void Select(WeaponId id)
    {
        if (!Owned.Contains(id) || id == Current) return;
        Current = id;
        _reload = 0f;
        _switchT = 0.35f;
        foreach (var kv in _models) kv.Value.Visible = Active && kv.Key == Current;
    }

    public void Reload()
    {
        var d = Defs[Current];
        if (!Active || Reloading || Mag[Current] >= d.Mag || Reserve[Current] <= 0) return;
        _reload = d.Reload;
    }

    public void AddSway(Vector2 mouse) => _sway += mouse * 0.00012f;

    /// <summary>Call every frame with trigger state. Returns true if a shot was fired.</summary>
    public bool Trigger(bool held, bool pressed, Camera3D cam, PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> exclude)
    {
        if (!Active || Reloading || _switchT > 0f || _cool > 0f) return false;
        var d = Defs[Current];
        if (!(d.Auto ? held : pressed)) return false;
        if (Mag[Current] <= 0)
        {
            if (pressed) { EmptyClick?.Invoke(); Reload(); }
            return false;
        }
        Mag[Current]--;
        _cool = d.Interval;
        _kick += d.Kick;
        _flashT = 0.05f;

        Vector3 origin = cam.GlobalPosition;
        Basis cb = cam.GlobalBasis;
        for (int p = 0; p < d.Pellets; p++)
        {
            float a = (float)_rng.NextDouble() * Mathf.Tau, r = (float)Math.Sqrt(_rng.NextDouble()) * d.Spread;
            Vector3 dir = (-cb.Z + cb.X * MathF.Cos(a) * r + cb.Y * MathF.Sin(a) * r).Normalized();
            var q = PhysicsRayQueryParameters3D.Create(origin, origin + dir * d.Range, Layers.Interior | Layers.Enemy, exclude);
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) continue;
            Vector3 point = (Vector3)hit["position"];
            if (hit["collider"].AsGodotObject() is IDamageable target) target.TakeDamage(d.Damage, point, dir);
            else Spark(point, (Vector3)hit["normal"]);
        }
        Shot?.Invoke(Current);
        Noise?.Invoke(origin, Current == WeaponId.Pistol ? 28f : 40f);
        return true;
    }

    private void Spark(Vector3 point, Vector3 normal)
    {
        if (_sparkMesh == null)
        {
            var b = new MeshBuilder();
            b.Rock(Vector3.Zero, new Vector3(0.05f, 0.05f, 0.05f), new Color(1f, 0.8f, 0.4f, 0.3f), 2, 0.6f);
            _sparkMesh = b.Build(Art.Machine);
        }
        var m = new MeshInstance3D { Mesh = _sparkMesh, TopLevel = true };
        AddChild(m);
        m.GlobalPosition = point + normal * 0.03f;
        _sparks.Add((m, 0.12f));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _cool = Mathf.Max(0f, _cool - dt);
        _switchT = Mathf.Max(0f, _switchT - dt);
        if (_reload > 0f)
        {
            _reload -= dt;
            if (_reload <= 0f)
            {
                var d = Defs[Current];
                int take = Math.Min(d.Mag - Mag[Current], Reserve[Current]);
                Mag[Current] += take;
                Reserve[Current] -= take;
                Reloaded?.Invoke(Current);
            }
        }

        // Viewmodel: kick, sway, bob, reload dip, switch raise.
        _kick = Mathf.MoveToward(_kick, 0f, dt * 0.9f);
        _sway = _sway.Lerp(Vector2.Zero, Mathf.Clamp(8f * dt, 0f, 1f));
        float speed = CarrierSpeed?.Invoke() ?? 0f;
        _bob += dt * speed * 2.2f;
        float reloadDip = _reload > 0f ? Mathf.Sin(Mathf.Clamp(1f - _reload / Defs[Current].Reload, 0f, 1f) * Mathf.Pi) * 0.12f : 0f;
        if (_models.TryGetValue(Current, out var model))
        {
            model.Position = Hip + new Vector3(-_sway.X + Mathf.Sin(_bob) * 0.008f, _sway.Y - reloadDip - _switchT * 0.4f + Mathf.Abs(Mathf.Cos(_bob)) * 0.006f, _kick * 0.6f);
            model.Rotation = new Vector3(_kick * 2.2f + reloadDip * 2f, -_sway.X * 2f, reloadDip * 1.5f);
        }

        _flashT -= dt;
        _flash.LightEnergy = _flashT > 0f ? 5f : 0f;
        _flashQuad.Visible = _flashT > 0f && Active;
        if (model != null) _flashQuad.Position = model.Position + new Vector3(0, 0.04f, Current == WeaponId.Rifle ? -0.55f : Current == WeaponId.Shotgun ? -0.58f : -0.22f);

        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var (m, t) = _sparks[i];
            t -= dt;
            if (t <= 0f) { m.QueueFree(); _sparks.RemoveAt(i); }
            else _sparks[i] = (m, t);
        }
    }
}
