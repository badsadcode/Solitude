using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Player;

namespace Solitude.Creatures;

/// <summary>
/// Station infestation: a dog-sized, many-legged thing that nests in dead corridors.
/// Procedural body (segmented abdomen, mandibled head, six two-joint legs on a tripod gait),
/// navmesh pathing, and a small state machine:
///   Dormant → (noise / sight / too close) → Hunt → Attack (wind-up, lunge) → … → Dead
/// It hears gunshots and running footsteps; it sees you only with line of sight.
/// Placeholder until Tom models the real thing — the AI does not care what it looks like.
/// </summary>
public partial class Crawler : CharacterBody3D, IDamageable
{
    public enum State { Dormant, Hunt, Attack, Search, Dead }

    public PlayerController Player;
    public int Index;                                // stable id within its station (for persistence)
    public Action<string, Vector3> Sound;            // "chitter", "screech", "hiss", "hurt", "die"
    public Action<Crawler> Killed;

    public State Current { get; private set; } = State.Dormant;
    public float Health = 70f;

    private NavigationAgent3D _agent;
    private Node3D _body, _head;
    private readonly List<(Node3D hip, Node3D knee, float phase, int side)> _legs = new();
    private Node3D _mandL, _mandR;
    private float _stuck;
    private float _t, _gait, _windup = -1f, _cooldown, _lostTimer, _chitterTimer, _repath, _hurtFlash;
    private Vector3 _lastKnown, _facing = Vector3.Forward;
    private readonly Random _rng = new();
    private ShaderMaterial _mat;

    private const float HuntSpeed = 3.7f, SearchSpeed = 3.0f, AttackRange = 1.7f, Damage = 14f;

    public override void _Ready()
    {
        CollisionLayer = Layers.Enemy;
        CollisionMask = Layers.Interior;
        FloorMaxAngle = Mathf.DegToRad(55f);
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.45f, Height = 1.0f }, Position = new Vector3(0, 0.5f, 0), Rotation = new Vector3(Mathf.Pi / 2f, 0, 0) });

        _agent = new NavigationAgent3D { PathDesiredDistance = 0.6f, TargetDesiredDistance = 1.2f, Radius = 0.45f, Height = 0.9f, AvoidanceEnabled = false };
        AddChild(_agent);

        if (Abyssal) BuildAbyssal();
        else
        {
            _mat = (ShaderMaterial)Art.Flora.Duplicate();
            _mat.SetShaderParameter("sway", 0f);
            _mat.SetShaderParameter("emission_color", new Color(0.85f, 1f, 0.9f));
            _mat.SetShaderParameter("emission_energy", 5f);
            BuildBody();
        }
        _chitterTimer = 2f + (float)_rng.NextDouble() * 6f;
    }

    private void BuildBody()
    {
        var shell = new Color(0.32f, 0.27f, 0.24f);
        var pale = new Color(0.62f, 0.55f, 0.48f);
        _body = new Node3D { Position = new Vector3(0, 0.55f, 0) };
        AddChild(_body);
        var mb = new MeshBuilder();
        mb.Rock(new Vector3(0, 0.05f, 0.45f), new Vector3(0.38f, 0.28f, 0.5f), shell, 11, 0.2f);   // abdomen
        mb.Rock(new Vector3(0, 0.1f, 0.95f), new Vector3(0.26f, 0.2f, 0.32f), shell.Shade(0.85f), 12, 0.25f);
        mb.Rock(new Vector3(0, 0.0f, 0.0f), new Vector3(0.3f, 0.22f, 0.32f), pale, 13, 0.15f);      // thorax
        for (int i = 0; i < 5; i++) // dorsal spines
            mb.Cylinder(new Vector3(0, 0.25f, 0.1f + i * 0.18f), 0.04f, 0f, 0.22f + (i % 2) * 0.1f, 4, shell.Shade(0.7f), caps: false);
        _body.AddChild(new MeshInstance3D { Mesh = mb.Build(_mat) });

        _head = new Node3D { Position = new Vector3(0, 0.02f, -0.3f) };
        _body.AddChild(_head);
        var hb = new MeshBuilder();
        hb.Rock(new Vector3(0, 0, -0.1f), new Vector3(0.2f, 0.15f, 0.22f), pale.Shade(0.9f), 14, 0.2f);
        for (int i = 0; i < 3; i++) // a cluster of small pale eyes
            foreach (int s in new[] { -1, 1 })
                hb.Rock(new Vector3(s * (0.06f + i * 0.04f), 0.08f + (i % 2) * 0.03f, -0.22f + i * 0.04f), new Vector3(0.025f, 0.025f, 0.025f), new Color(1f, 1f, 1f, 0.4f), 20 + i, 0.05f);
        _head.AddChild(new MeshInstance3D { Mesh = hb.Build(_mat) });
        foreach (int s in new[] { -1, 1 })
        {
            var m = new Node3D { Position = new Vector3(s * 0.08f, -0.05f, -0.28f) };
            var mm = new MeshBuilder();
            mm.BoxOriented(new Transform3D(new Basis(Vector3.Up, s * 0.4f), new Vector3(s * 0.04f, 0, -0.1f)), new Vector3(0.03f, 0.03f, 0.22f), shell.Shade(0.6f));
            m.AddChild(new MeshInstance3D { Mesh = mm.Build(_mat) });
            _head.AddChild(m);
            if (s < 0) _mandL = m; else _mandR = m;
        }

        // Legs: hip (yaw/lift) → upper segment → knee → lower segment
        for (int i = 0; i < 6; i++)
        {
            int side = i < 3 ? -1 : 1;
            float z = -0.15f + (i % 3) * 0.3f;
            var hip = new Node3D { Position = new Vector3(side * 0.22f, -0.02f, z) };
            _body.AddChild(hip);
            var up = new MeshBuilder();
            up.BoxOriented(new Transform3D(new Basis(Vector3.Forward, side * -0.6f), new Vector3(side * 0.25f, 0.12f, 0)), new Vector3(0.5f, 0.05f, 0.05f), shell);
            hip.AddChild(new MeshInstance3D { Mesh = up.Build(_mat) });
            var knee = new Node3D { Position = new Vector3(side * 0.48f, 0.26f, 0) };
            hip.AddChild(knee);
            var lo = new MeshBuilder();
            lo.BoxOriented(new Transform3D(new Basis(Vector3.Forward, side * 1.1f), new Vector3(side * 0.18f, -0.3f, 0)), new Vector3(0.04f, 0.7f, 0.04f), pale.Shade(0.7f));
            knee.AddChild(new MeshInstance3D { Mesh = lo.Build(_mat) });
            // Tripod gait: legs 0,2,4 vs 1,3,5 alternate.
            _legs.Add((hip, knee, (i % 2 == 0 ? 0f : Mathf.Pi) + (i >= 3 ? Mathf.Pi : 0f), side));
        }
    }

    // ------------------------------------------------------------------ senses

    /// <summary>A sound happened. Loud things wake the nest.</summary>
    public string Debug => $"{Current} vel={new Vector2(Velocity.X, Velocity.Z).Length():0.0} floor={IsOnFloor()} next={(_agent.GetNextPathPosition() - GlobalPosition).Length():0.0}m finished={_agent.IsNavigationFinished()} reachable={_agent.IsTargetReachable()} lost={_lostTimer:0.0}";

    public void Hear(Vector3 pos, float radius)
    {
        if (Current == State.Dead) return;
        if (GlobalPosition.DistanceTo(pos) > radius) return;
        _lastKnown = pos;
        if (Current == State.Dormant || Current == State.Search) BeginHunt();
    }

    private bool CanSee(Vector3 target)
    {
        var space = GetWorld3D().DirectSpaceState;
        var q = PhysicsRayQueryParameters3D.Create(GlobalPosition + Vector3.Up * 0.7f, target + Vector3.Up * 1.4f, Layers.Interior);
        return space.IntersectRay(q).Count == 0;
    }

    private void BeginHunt()
    {
        if (Current != State.Hunt) Sound?.Invoke("screech", GlobalPosition);
        Current = State.Hunt;
        _lostTimer = 0f;
    }

    public void TakeDamage(float amount, Vector3 point, Vector3 direction)
    {
        if (Current == State.Dead) return;
        Health -= amount;
        _hurtFlash = 0.15f;
        Velocity += direction * 1.5f;
        if (Health <= 0f) { Die(); return; }
        Sound?.Invoke("hurt", GlobalPosition);
        if (Player != null) _lastKnown = Player.GlobalPosition;
        BeginHunt();
    }

    private void Die()
    {
        Current = State.Dead;
        Sound?.Invoke("die", GlobalPosition);
        CollisionLayer = 0;
        Killed?.Invoke(this);
    }

    /// <summary>Restore as an already-dead corpse (persistence across visits).</summary>
    public void SetCorpse()
    {
        Current = State.Dead;
        CollisionLayer = 0;
        _t = 10f;
    }

    // ------------------------------------------------------------------ brain

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        Vector3 v = Velocity;
        if (!IsOnFloor()) v.Y -= 9.8f * dt; else v.Y = 0f;
        Vector3 desired = Vector3.Zero;
        float speed = 0f;

        if (Current != State.Dead && Player != null && !Player.Dead && Player.InStation)
        {
            Vector3 pp = Player.GlobalPosition;
            float d = GlobalPosition.DistanceTo(pp);
            bool sees = d < 16f && CanSee(pp);

            switch (Current)
            {
                case State.Dormant:
                    if ((sees && d < 12f) || d < 3.5f) { _lastKnown = pp; BeginHunt(); }
                    _chitterTimer -= dt;
                    if (_chitterTimer <= 0f) { Sound?.Invoke("chitter", GlobalPosition); _chitterTimer = 4f + (float)_rng.NextDouble() * 9f; }
                    break;
                case State.Hunt:
                    if (sees) { _lastKnown = pp; _lostTimer = 0f; } else _lostTimer += dt;
                    // Without sight it keeps travelling to where it last heard/saw you; it only gives up
                    // (and slows to a search) after a long while, or once it gets there and finds nothing.
                    if (_lostTimer > 20f || (!sees && GlobalPosition.DistanceTo(_lastKnown) < 2f)) { Current = State.Search; break; }
                    if (d < AttackRange && sees && _cooldown <= 0f) { Current = State.Attack; _windup = 0.35f; Sound?.Invoke("hiss", GlobalPosition); break; }
                    desired = PathTo(sees ? pp : _lastKnown, dt);
                    speed = HuntSpeed;
                    break;
                case State.Attack:
                    _windup -= dt;
                    _facing = (pp - GlobalPosition) with { Y = 0 };
                    if (_windup <= 0f)
                    {
                        // Lunge
                        v += _facing.Normalized() * 5f;
                        if (GlobalPosition.DistanceTo(pp) < AttackRange + 0.5f) Player.Damage(Damage);
                        _cooldown = 1.1f;
                        Current = State.Hunt;
                    }
                    break;
                case State.Search:
                    desired = PathTo(_lastKnown, dt);
                    speed = SearchSpeed;
                    if (sees) BeginHunt();
                    if (GlobalPosition.DistanceTo(_lastKnown) < 1.5f) Current = State.Dormant;
                    break;
            }
        }
        _cooldown = Mathf.Max(0f, _cooldown - dt);

        Vector3 hv = new Vector3(v.X, 0, v.Z).Lerp(desired * speed, Mathf.Clamp(6f * dt, 0f, 1f));
        if (Current == State.Dead) hv = hv.Lerp(Vector3.Zero, Mathf.Clamp(4f * dt, 0f, 1f));
        v.X = hv.X; v.Z = hv.Z;
        Velocity = v;
        Stepper.TryStepUp(this, dt, 0.6f);
        Vector3 before = GlobalPosition;
        MoveAndSlide();

        // Stuck detector: wants to move but isn't getting anywhere → hop and jink sideways.
        float wanted = desired.Length() * speed;
        float made = (GlobalPosition - before).Length() / Mathf.Max(dt, 1e-4f);
        _stuck = wanted > 0.5f && made < wanted * 0.25f ? _stuck + dt : 0f;
        if (_stuck > 0.8f && IsOnFloor())
        {
            Vector3 side = new Vector3(-desired.Z, 0, desired.X) * (_rng.NextDouble() < 0.5 ? -1f : 1f);
            Velocity = new Vector3(side.X * 2f, 4.5f, side.Z * 2f);
            _stuck = 0f;
            _repath = 0f;
        }

        if (desired.LengthSquared() > 0.01f) _facing = _facing.Lerp(desired, Mathf.Clamp(8f * dt, 0f, 1f));
        if (_facing.LengthSquared() > 0.01f)
            Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, Mathf.Atan2(-_facing.X, -_facing.Z), Mathf.Clamp(10f * dt, 0f, 1f)), 0);

        Animate(dt, hv.Length());
    }

    private Vector3 PathTo(Vector3 target, float dt)
    {
        _repath -= dt;
        if (_repath <= 0f) { _agent.TargetPosition = target; _repath = 0.25f; }
        Vector3 next = _agent.IsNavigationFinished() ? target : _agent.GetNextPathPosition();
        Vector3 dir = next - GlobalPosition;
        dir.Y = 0;
        // No navmesh yet (or off-mesh): go straight.
        if (dir.LengthSquared() < 0.01f) { dir = target - GlobalPosition; dir.Y = 0; }
        return dir.LengthSquared() > 0.0001f ? dir.Normalized() : Vector3.Zero;
    }

    // ------------------------------------------------------------------ animation

    private void Animate(float dt, float speed)
    {
        if (Abyssal) { AnimateAbyssal(dt, speed); return; }
        if (Current == State.Dead)
        {
            // Legs curl in, body drops and rolls a little.
            float k = Mathf.Clamp(_t * 2f, 0f, 1f);
            _body.Position = _body.Position.Lerp(new Vector3(0, 0.18f, 0), Mathf.Clamp(3f * dt, 0f, 1f));
            _body.Rotation = _body.Rotation.Lerp(new Vector3(0, 0, 0.5f), Mathf.Clamp(2f * dt, 0f, 1f));
            foreach (var (hip, knee, _, side) in _legs)
            {
                hip.Rotation = hip.Rotation.Lerp(new Vector3(0, 0, side * 0.8f), k * 0.1f);
                knee.Rotation = knee.Rotation.Lerp(new Vector3(0, 0, side * -1.2f), k * 0.1f);
            }
            _mat.SetShaderParameter("emission_energy", Mathf.MoveToward((float)_mat.GetShaderParameter("emission_energy"), 0f, dt * 3f));
            return;
        }

        float rate = 1.5f + speed * 3.2f;
        _gait += dt * rate * (speed > 0.2f ? 1f : 0.15f);
        float lift = speed > 0.2f ? 0.35f : 0.05f;
        foreach (var (hip, knee, phase, side) in _legs)
        {
            float s = Mathf.Sin(_gait + phase);
            hip.Rotation = new Vector3(0, s * 0.45f * Mathf.Clamp(speed / 2f, 0.15f, 1f), side * Mathf.Max(0f, Mathf.Cos(_gait + phase)) * lift);
            knee.Rotation = new Vector3(0, 0, -side * Mathf.Max(0f, Mathf.Cos(_gait + phase)) * lift * 0.8f);
        }
        float bodyBob = Mathf.Sin(_gait * 2f) * 0.03f * Mathf.Clamp(speed, 0f, 1f);
        float crouch = Current == State.Attack ? -0.15f : Current == State.Dormant ? -0.12f + Mathf.Sin(_t * 0.7f) * 0.02f : 0f;
        _body.Position = new Vector3(0, 0.55f + bodyBob + crouch, 0);
        _body.Rotation = new Vector3(Current == State.Attack ? 0.25f : 0f, 0, Mathf.Sin(_gait) * 0.04f);
        float jaw = Current == State.Attack ? 0.7f : Current == State.Hunt ? 0.25f + Mathf.Sin(_t * 18f) * 0.1f : 0.05f + Mathf.Sin(_t * 1.3f) * 0.05f;
        _mandL.Rotation = new Vector3(0, jaw, 0);
        _mandR.Rotation = new Vector3(0, -jaw, 0);
        _head.Rotation = new Vector3(Mathf.Sin(_t * 2.3f) * 0.08f, Mathf.Sin(_t * 1.1f) * 0.15f, 0);

        _hurtFlash = Mathf.Max(0f, _hurtFlash - dt);
        _mat.SetShaderParameter("tint", _hurtFlash > 0f ? new Color(2f, 0.7f, 0.6f) : Colors.White);
    }
}
