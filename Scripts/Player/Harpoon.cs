using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Creatures;

namespace Solitude.Player;

/// <summary>
/// The hard suit's harpoon gun (Tom: "the movement of the harpoon is delayed... two modes: a shocker,
/// and shooting the harpoon itself").
///
/// Heavy in the water: the gun doesn't follow the helmet rigidly. It trails behind your view, catches
/// up a beat later, rolls into turns and bobs as you swim — and it fires where the GUN points, not
/// where you look, so the reticle drifts. Q swaps modes, LMB fires.
///
///   SPEAR: a barbed spear on a tether. It flies (water drag, a little drop), sticks in rock or in a
///          creature, then the reel ratchets it back for the next shot. 25 m of line.
///   PROD:  short-range jab; the prongs crackle when charged and the shock stuns whatever it touches,
///          making it break off its attack. Recharges in a few seconds. Sometimes it bites you too.
/// </summary>
public partial class Harpoon : Node3D
{
    public enum Mode { Spear, Prod }
    private enum SpearState { Ready, Flying, Stuck, Reeling }

    public Diver Diver;
    public Func<IEnumerable<IHarpoonable>> Targets;
    public Action<string, Vector3> Sound;
    public Action<string> Message;
    public Action<float> SelfShock;

    public Mode Current { get; private set; } = Mode.Spear;
    public float Charge { get; private set; } = 1f;   // prod, 0..1
    public bool SpearReady => _spear == SpearState.Ready;
    /// <summary>Where the gun is actually pointing (for the reticle).</summary>
    public Vector3 AimPoint => _muzzle.GlobalPosition - _rig.GlobalBasis.Z * 18f;
    public string Status => Current == Mode.Spear
        ? _spear switch { SpearState.Ready => "SPEAR READY", SpearState.Reeling => "REELING", _ => "LINE OUT" }
        : Charge >= 1f ? "PROD CHARGED" : $"PROD {Charge * 100f:0}%";

    private const float SpearSpeed = 26f, ProdReach = 2.6f;
    /// <summary>Modules: reel motor (line length, reel speed), prod capacitor (recharge, insulation).</summary>
    public float LineLength = 25f, ReelSpeed = 11f, ProdRecharge = 3f, SelfZapChance = 0.12f;
    private static readonly Vector3 HoldOffset = new(0.22f, -0.22f, -0.42f);

    private Node3D _rig, _muzzle, _spearNode, _prongs;
    private MeshInstance3D _line, _arcMesh;
    private ImmediateMesh _lineMesh, _arcIm;
    private OmniLight3D _flash;
    private StandardMaterial3D _readyLamp;
    private Quaternion _q = Quaternion.Identity;
    private Vector3 _lastFwd = Vector3.Forward, _bobVel;
    private float _roll, _pitchLag, _kick, _swap = 1f, _jab, _flashT, _t, _stuckT, _reelClick, _arcT;
    private bool _wasDown, _swapPending;
    private SpearState _spear;
    private Vector3 _sPos, _sVel;
    private Node3D _stuckTo;
    private Vector3 _stuckLocal, _stuckDirLocal;
    private readonly Random _rng = new();

    public override void _Ready()
    {
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        BuildGun();

        // The spear is its own top-level node: on the rail when loaded, in the world when fired.
        _spearNode = new Node3D { Name = "Spear", TopLevel = true };
        AddChild(_spearNode);
        _spearNode.AddChild(new MeshInstance3D { Mesh = SpearMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        _lineMesh = new ImmediateMesh();
        _line = new MeshInstance3D
        {
            Name = "Tether", TopLevel = true, Mesh = _lineMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.75f, 0.72f, 0.6f), VertexColorUseAsAlbedo = false },
        };
        AddChild(_line);
        SetActive(false);
    }

    public void SetActive(bool on)
    {
        Visible = on;
        SetProcess(on);
        if (!on) return;
        _spear = SpearState.Ready;
        _stuckTo = null;
        _q = Diver.Camera.GlobalBasis.GetRotationQuaternion();
        _lineMesh.ClearSurfaces();
        Charge = 1f;
    }

    // ------------------------------------------------------------------ model

    private void BuildGun()
    {
        var metal = Art.World(Art.FlatTexture(new Color(0.4f, 0.43f, 0.45f)), Colors.White, roughness: 0.5f, metallic: 0.6f, jitter: 200f);
        var paint = Art.World(Art.FlatTexture(new Color(0.75f, 0.55f, 0.12f)), Colors.White, roughness: 0.6f, jitter: 200f);
        var rubber = Art.World(Art.FlatTexture(new Color(0.06f, 0.06f, 0.06f)), Colors.White, roughness: 0.9f, jitter: 200f);

        void Part(Mesh m, Material mat, Vector3 pos, Vector3 rot = default, Node3D parent = null)
        {
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat, Position = pos, Rotation = rot, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            (parent ?? _rig).AddChild(mi);
        }
        var along = new Vector3(Mathf.Pi / 2f, 0, 0);   // cylinders lie along Z
        // Rail / barrel, body, the gas bottle under it, grip, the reel drum on the side.
        Part(new BoxMesh { Size = new Vector3(0.035f, 0.03f, 0.62f) }, metal, new Vector3(0, 0.035f, -0.2f));
        Part(new BoxMesh { Size = new Vector3(0.07f, 0.075f, 0.26f) }, metal, new Vector3(0, 0, 0.02f));
        Part(new CylinderMesh { TopRadius = 0.028f, BottomRadius = 0.028f, Height = 0.32f, RadialSegments = 8 }, paint, new Vector3(0, -0.045f, -0.12f), along);
        Part(new BoxMesh { Size = new Vector3(0.04f, 0.13f, 0.05f) }, rubber, new Vector3(0, -0.09f, 0.08f), new Vector3(-0.25f, 0, 0));
        Part(new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.045f, Height = 0.03f, RadialSegments = 10 }, metal, new Vector3(0.05f, 0.01f, 0.0f), new Vector3(0, 0, Mathf.Pi / 2f));
        Part(new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.03f, Height = 0.035f, RadialSegments = 8 }, paint, new Vector3(0.068f, 0.01f, 0.0f), new Vector3(0, 0, Mathf.Pi / 2f));

        // A little status lamp on the back of the body: green = ready, amber = line out / charging.
        _readyLamp = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 1f, 0.5f) };
        Part(new BoxMesh { Size = new Vector3(0.016f, 0.012f, 0.012f) }, _readyLamp, new Vector3(0, 0.043f, 0.15f));
        _rig.AddChild(new OmniLight3D { Position = new Vector3(0, 0.08f, 0.1f), LightColor = new Color(0.7f, 0.8f, 0.85f), OmniRange = 0.6f, LightEnergy = 0.22f, ShadowEnabled = false });

        _muzzle = new Node3D { Name = "Muzzle", Position = new Vector3(0, 0.035f, -0.52f) };
        _rig.AddChild(_muzzle);

        // Prod head: two prongs that swing out in front of the muzzle in prod mode.
        _prongs = new Node3D { Name = "Prongs", Position = new Vector3(0, 0.035f, -0.5f) };
        _rig.AddChild(_prongs);
        var copper = Art.World(Art.FlatTexture(new Color(0.85f, 0.5f, 0.3f)), Colors.White, roughness: 0.3f, metallic: 0.8f, jitter: 200f);
        Part(new BoxMesh { Size = new Vector3(0.012f, 0.012f, 0.16f) }, copper, new Vector3(-0.03f, 0, -0.08f), default, _prongs);
        Part(new BoxMesh { Size = new Vector3(0.012f, 0.012f, 0.16f) }, copper, new Vector3(0.03f, 0, -0.08f), default, _prongs);

        _arcIm = new ImmediateMesh();
        _arcMesh = new MeshInstance3D
        {
            Mesh = _arcIm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.6f, 0.85f, 1f) },
        };
        _prongs.AddChild(_arcMesh);
        _flash = new OmniLight3D { Position = new Vector3(0, 0, -0.18f), LightColor = new Color(0.5f, 0.75f, 1f), OmniRange = 5f, LightEnergy = 0f };
        _prongs.AddChild(_flash);
    }

    private static Mesh SpearMesh()
    {
        // Shaft along -Z from the origin (the tail sits at the origin), a barbed head at the front.
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Box(Vector3 c, Vector3 h, Color col)
        {
            Vector3[] v =
            {
                c + new Vector3(-h.X, -h.Y, -h.Z), c + new Vector3(h.X, -h.Y, -h.Z), c + new Vector3(h.X, h.Y, -h.Z), c + new Vector3(-h.X, h.Y, -h.Z),
                c + new Vector3(-h.X, -h.Y, h.Z), c + new Vector3(h.X, -h.Y, h.Z), c + new Vector3(h.X, h.Y, h.Z), c + new Vector3(-h.X, h.Y, h.Z),
            };
            int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } };
            foreach (var f in faces)
            {
                Vector3 n = (v[f[1]] - v[f[0]]).Cross(v[f[2]] - v[f[0]]).Normalized();
                foreach (int i in new[] { f[0], f[1], f[2], f[0], f[2], f[3] }) { st.SetColor(col); st.SetNormal(n); st.AddVertex(v[i]); }
            }
        }
        var steel = new Color(0.7f, 0.72f, 0.72f);
        Box(new Vector3(0, 0, -0.33f), new Vector3(0.006f, 0.006f, 0.33f), steel);
        // Head: a tapering pyramid.
        Vector3 tip = new(0, 0, -0.78f);
        Vector3[] b = { new(-0.016f, -0.016f, -0.66f), new(0.016f, -0.016f, -0.66f), new(0.016f, 0.016f, -0.66f), new(-0.016f, 0.016f, -0.66f) };
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = b[i], c = b[(i + 1) % 4];
            Vector3 n = (c - a).Cross(tip - a).Normalized();
            st.SetColor(steel); st.SetNormal(-n); st.AddVertex(a);
            st.SetColor(steel); st.SetNormal(-n); st.AddVertex(tip);
            st.SetColor(steel); st.SetNormal(-n); st.AddVertex(c);
        }
        // Barbs folded back from the head, and a red flight at the tail so you can find it in the murk.
        Box(new Vector3(0.018f, 0, -0.62f), new Vector3(0.003f, 0.003f, 0.04f), steel);
        Box(new Vector3(-0.018f, 0, -0.62f), new Vector3(0.003f, 0.003f, 0.04f), steel);
        Box(new Vector3(0, 0, -0.03f), new Vector3(0.002f, 0.02f, 0.03f), new Color(0.8f, 0.15f, 0.1f));
        var mat = Art.World(Art.FlatTexture(Colors.White), Colors.White, roughness: 0.4f, metallic: 0.7f, jitter: 200f);
        st.SetMaterial(mat);
        return st.Commit();
    }

    // ------------------------------------------------------------------ per frame

    public override void _Process(double delta)
    {
        if (Diver == null || !Diver.Active) return;
        float dt = (float)Math.Min(delta, 0.1);
        _t += dt;
        Sway(dt);
        HandleInput();
        UpdateSpear(dt);
        UpdateProd(dt);
    }

    /// <summary>The heavy-in-water lag: the gun trails the helmet, rolls into turns, bobs with strokes.</summary>
    private void Sway(float dt)
    {
        var cam = Diver.Camera.GlobalTransform;
        Quaternion target = cam.Basis.GetRotationQuaternion();
        _q = _q.Slerp(target, 1f - MathF.Exp(-6.5f * dt)).Normalized();

        // How far the gun is behind the view: yaw lag rolls it, pitch lag tips it.
        Vector3 camFwd = -cam.Basis.Z, gunFwd = new Basis(_q) * Vector3.Forward;
        Vector3 lagLocal = cam.Basis.Inverse() * gunFwd;
        _roll = Mathf.Lerp(_roll, Mathf.Clamp(lagLocal.X * 2.2f, -0.6f, 0.6f), 1f - MathF.Exp(-5f * dt));
        _pitchLag = Mathf.Lerp(_pitchLag, Mathf.Clamp(lagLocal.Y * 0.8f, -0.3f, 0.3f), 1f - MathF.Exp(-5f * dt));
        _lastFwd = camFwd;

        // Bob with the strokes; drag back against the swim velocity.
        Vector3 v = cam.Basis.Inverse() * Diver.Velocity;
        float swim = Mathf.Clamp(Diver.Velocity.Length() / 2.6f, 0f, 1f);
        Vector3 bob = new(MathF.Sin(_t * 2.1f) * 0.012f * (0.3f + swim), MathF.Sin(_t * 4.2f) * 0.008f * (0.3f + swim) + MathF.Sin(_t * 0.7f) * 0.004f, 0f);
        _bobVel = _bobVel.Lerp(-v * 0.025f, 1f - MathF.Exp(-3f * dt));

        _kick = Mathf.MoveToward(_kick, 0f, dt * 2.5f);
        _jab = Mathf.MoveToward(_jab, 0f, dt * 4f);
        // Mode swap: the gun dips out of view, changes, comes back up.
        if (_swap < 1f)
        {
            float before = _swap;
            _swap = Mathf.Min(1f, _swap + dt * 1.8f);
            if (before < 0.5f && _swap >= 0.5f && _swapPending) { _swapPending = false; Current = Current == Mode.Spear ? Mode.Prod : Mode.Spear; }
        }
        float dip = MathF.Sin(_swap * Mathf.Pi);   // 0 → 1 → 0 across the swap

        Vector3 offset = HoldOffset + bob + _bobVel.LimitLength(0.08f) + new Vector3(0, -dip * 0.25f, _kick * 0.12f - _jab * 0.3f);
        Basis basis = new Basis(_q) * Basis.FromEuler(new Vector3(_pitchLag + _kick * 0.35f - dip * 0.6f, 0, _roll));
        GlobalTransform = new Transform3D(basis, cam.Origin + new Basis(_q) * offset);

        // Prongs fold out in prod mode.
        float prongTarget = Current == Mode.Prod ? 1f : 0f;
        _prongs.Scale = new Vector3(1f, 1f, Mathf.Lerp(_prongs.Scale.Z, Mathf.Max(0.05f, prongTarget), 1f - MathF.Exp(-10f * dt)));
    }

    private void HandleInput()
    {
        bool down = Input.IsMouseButtonPressed(MouseButton.Left) && Input.MouseMode == Input.MouseModeEnum.Captured;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (Input.IsActionJustPressed(GameInput.HarpoonMode)) Swap();
        if (pressed) Fire();
    }

    /// <summary>Pull the trigger (LMB; public for the capture harness).</summary>
    public void Fire()
    {
        if (_swap < 1f) return;
        if (Current == Mode.Spear) FireSpear();
        else Jab();
    }

    /// <summary>Spear ⇄ shock prod (Q).</summary>
    public void Swap()
    {
        if (_swap < 1f) return;
        if (_spear != SpearState.Ready) { Message?.Invoke("Reel the spear in first."); return; }
        _swap = 0f;
        _swapPending = true;
        Sound?.Invoke("reload", GlobalPosition);
    }

    // ------------------------------------------------------------------ spear

    private void FireSpear()
    {
        if (_spear != SpearState.Ready) { Sound?.Invoke("empty", GlobalPosition); return; }
        _spear = SpearState.Flying;
        _sPos = _muzzle.GlobalPosition;
        _sVel = -_rig.GlobalBasis.Z * SpearSpeed + Diver.Velocity;
        _kick = 1f;
        Diver.Velocity += _rig.GlobalBasis.Z * 0.35f;   // a little recoil through the water
        Sound?.Invoke("harpoon", _muzzle.GlobalPosition);
    }

    private void UpdateSpear(float dt)
    {
        Vector3 muzzle = _muzzle.GlobalPosition;
        switch (_spear)
        {
            case SpearState.Ready:
                _spearNode.Visible = Current == Mode.Spear || _swap < 0.5f;
                _spearNode.GlobalTransform = new Transform3D(_rig.GlobalBasis, muzzle + _rig.GlobalBasis.Z * 0.5f);
                _lineMesh.ClearSurfaces();
                return;

            case SpearState.Flying:
            {
                Vector3 next = _sPos + _sVel * dt;
                _sVel -= _sVel * Mathf.Clamp(0.9f * dt, 0f, 1f);       // water drag
                _sVel += Vector3.Down * 1.8f * dt;                    // it's heavier than water
                // Creatures first (their bodies are long capsules, not physics shapes).
                if (Targets != null)
                    foreach (var c in Targets())
                    {
                        if (c?.Body == null || !c.Body.IsInsideTree()) continue;
                        if (Hit.ToSegment(next, c.Head, c.Tail) < c.BodyRadius || Hit.ToSegment(_sPos, c.Head, c.Tail) < c.BodyRadius)
                        {
                            c.Hurt(25f, Diver.GlobalPosition, false);
                            StickTo(c.Body, next);
                            Sound?.Invoke("spearhit", next);
                            Message?.Invoke("Hit. It did not like that.");
                            return;
                        }
                    }
                var space = GetWorld3D().DirectSpaceState;
                var q = PhysicsRayQueryParameters3D.Create(_sPos, next + _sVel.Normalized() * 0.75f, Layers.World | Layers.Sub);
                var hit = space.IntersectRay(q);
                if (hit.Count > 0)
                {
                    Vector3 p = (Vector3)hit["position"] - _sVel.Normalized() * 0.6f;
                    StickTo(hit["collider"].As<Node3D>(), p);
                    Sound?.Invoke("knock", p);
                    return;
                }
                _sPos = next;
                if (_sPos.DistanceTo(muzzle) > LineLength || _sVel.Length() < 2f) { _spear = SpearState.Reeling; Sound?.Invoke("reel", muzzle); }
                _spearNode.GlobalTransform = new Transform3D(Basis.LookingAt(_sVel.Normalized(), Mathf.Abs(_sVel.Normalized().Y) > 0.98f ? Vector3.Right : Vector3.Up), _sPos);
                break;
            }

            case SpearState.Stuck:
                if (_stuckTo != null && GodotObject.IsInstanceValid(_stuckTo) && _stuckTo.IsInsideTree())
                {
                    _sPos = _stuckTo.ToGlobal(_stuckLocal);
                    Vector3 dir = (_stuckTo.GlobalBasis * _stuckDirLocal).Normalized();
                    _spearNode.GlobalTransform = new Transform3D(Basis.LookingAt(dir, Mathf.Abs(dir.Y) > 0.98f ? Vector3.Right : Vector3.Up), _sPos);
                    // A big animal on the end of your line drags you after it — briefly.
                    if (_stuckTo is IHarpoonable && _sPos.DistanceTo(muzzle) > LineLength * 0.8f)
                        Diver.Velocity += (_sPos - muzzle).Normalized() * 3f * dt;
                }
                _stuckT -= dt;
                if (_stuckT <= 0f || _sPos.DistanceTo(muzzle) > LineLength + 2f) { _spear = SpearState.Reeling; _stuckTo = null; }
                break;

            case SpearState.Reeling:
            {
                Vector3 to = muzzle + _rig.GlobalBasis.Z * 0.5f;
                Vector3 d = to - _sPos;
                float step = ReelSpeed * dt;
                if (d.Length() <= step + 0.05f) { _spear = SpearState.Ready; Sound?.Invoke("reload", muzzle); return; }
                _sPos += d.Normalized() * step;
                // The spear trails nose-out on its way back in.
                _spearNode.GlobalTransform = new Transform3D(Basis.LookingAt(-d.Normalized(), Mathf.Abs(d.Normalized().Y) > 0.98f ? Vector3.Right : Vector3.Up), _sPos);
                _reelClick -= dt;
                if (_reelClick <= 0f) { Sound?.Invoke("reel", muzzle); _reelClick = 0.09f; }
                break;
            }
        }
        DrawLine(muzzle);
    }

    private void StickTo(Node3D node, Vector3 at)
    {
        _spear = SpearState.Stuck;
        _sPos = at;
        _stuckTo = node;
        _stuckT = node is IHarpoonable ? 1.4f : 0.7f;
        Vector3 dir = _sVel.LengthSquared() > 0.01f ? _sVel.Normalized() : -_rig.GlobalBasis.Z;
        if (node != null) { _stuckLocal = node.ToLocal(at); _stuckDirLocal = node.GlobalBasis.Inverse() * dir; }
        else { _stuckLocal = at; _stuckDirLocal = dir; }
    }

    /// <summary>The tether: a sagging line while slack, straight when taut. Single-pixel lines suit the PS1 look.</summary>
    private void DrawLine(Vector3 muzzle)
    {
        _lineMesh.ClearSurfaces();
        Vector3 tail = _sPos;
        float dist = muzzle.DistanceTo(tail);
        float sag = Mathf.Clamp(1f - dist / LineLength, 0f, 1f) * dist * 0.04f + 0.01f;
        if (_spear == SpearState.Reeling) sag *= 0.3f;
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        const int n = 16;
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n;
            Vector3 p = muzzle.Lerp(tail, u) + Vector3.Down * sag * 4f * u * (1f - u);
            p += new Vector3(MathF.Sin(_t * 3f + u * 9f), 0, MathF.Cos(_t * 2.3f + u * 7f)) * 0.03f * u * (1f - u) * 4f;
            _lineMesh.SurfaceAddVertex(p);
        }
        _lineMesh.SurfaceEnd();
    }

    // ------------------------------------------------------------------ prod

    private void Jab()
    {
        _jab = 1f;
        if (Charge < 1f) { Sound?.Invoke("empty", GlobalPosition); return; }
        Charge = 0f;
        _flashT = 0.35f;
        Vector3 from = _muzzle.GlobalPosition, fwd = -_rig.GlobalBasis.Z;
        IHarpoonable best = null;
        Vector3 at = from;
        if (Targets != null)
            foreach (var c in Targets())
            {
                if (c?.Body == null || !c.Body.IsInsideTree()) continue;
                for (float s = 0f; s <= ProdReach; s += 0.2f)
                {
                    Vector3 p = from + fwd * s;
                    if (Hit.ToSegment(p, c.Head, c.Tail) < c.BodyRadius + 0.5f) { best = c; at = p; break; }
                }
                if (best != null) break;
            }
        Sound?.Invoke("shock", at);
        if (best != null)
        {
            best.Hurt(8f, Diver.GlobalPosition, true);
            Message?.Invoke("ZAP. It convulses and veers away.");
        }
        // Salt water is a conductor. Usually the suit's insulation holds.
        if (_rng.NextDouble() < SelfZapChance)
        {
            SelfShock?.Invoke(5f);
            Message?.Invoke("The prod bites you too. Your fillings hum.");
        }
    }

    private void UpdateProd(float dt)
    {
        if (Charge < 1f) Charge = Mathf.Min(1f, Charge + dt / ProdRecharge);
        bool ready = Current == Mode.Spear ? _spear == SpearState.Ready : Charge >= 1f;
        _readyLamp.AlbedoColor = ready ? new Color(0.3f, 1f, 0.5f) : new Color(1f, 0.55f, 0.1f) * (MathF.Sin(_t * 12f) > 0f ? 1f : 0.4f);
        _flashT -= dt;
        bool live = Current == Mode.Prod && _swap >= 1f;
        // Charged prongs crackle now and then; a discharge is a burst of blue.
        _arcT -= dt;
        bool arc = _flashT > 0f || (live && Charge >= 1f && _arcT < 0.06f);
        if (_arcT <= 0f) _arcT = 0.4f + (float)_rng.NextDouble() * 1.4f;
        _flash.LightEnergy = _flashT > 0f ? 3f + 4f * (float)_rng.NextDouble() : arc ? 0.6f : 0f;
        _arcIm.ClearSurfaces();
        if (!arc) return;
        _arcIm.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        Vector3 a = new(-0.03f, 0, -0.16f), b = new(0.03f, 0, -0.16f);
        float reach = _flashT > 0f ? 0.6f : 0.01f;
        for (int i = 0; i <= 6; i++)
        {
            float u = i / 6f;
            Vector3 p = a.Lerp(b, u) + new Vector3(0, (float)(_rng.NextDouble() - 0.5) * 0.03f, -(float)_rng.NextDouble() * reach * MathF.Sin(u * Mathf.Pi));
            _arcIm.SurfaceAddVertex(p);
        }
        _arcIm.SurfaceEnd();
    }
}
