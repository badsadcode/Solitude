using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.Core;
using Solitude.Player;
using Solitude.Sub;
using Solitude.Systems;

namespace Solitude.Interior;

/// <summary>
/// Things that go wrong aboard (Tom: "hull breaches and knocks, steam off the pipes, water dripping").
///
/// Breaches: hard impacts, diving below test depth, or a badly damaged hull can spring a leak in
///   one of the five compartments. Water sprays in, the room floods (slowing you, and the boat gets
///   heavier), a red alarm flashes, the klaxon sounds. Hold E on the breach to weld it (sealant or
///   a patch kit); bilge pumps then drain the room using power. Water above ~0.6 m spills through
///   open hatches; seal a hatch to stop it.
/// Steam: pipes vent hissing bursts, more under stress, damage and high engine output.
/// Drips: water drips from the ceiling as the hull weakens.
/// Knocks: deep, slow and alone, something knocks on the hull. Three times. Nothing on sonar.
/// All coordinates are interior-local (SubInterior's space).
/// </summary>
public partial class HullEvents : Node3D
{
    public SubInterior Interior;
    public Submarine Sub;
    public ShipSystems Systems;
    public AudioDirector Audio;
    public PlayerController Player;
    public Action<string> Message;
    public Action<string, Vector3> Sound;       // spatial sound at a GLOBAL position
    public Func<bool> Aboard;                   // captain inside the boat (not station, not diving)
    public Func<bool> Docked;

    // Compartments along the boat: bridge, sensor bay, galley, quarters, engineering.
    public static readonly string[] RoomNames = { "BRIDGE", "SENSOR BAY", "GALLEY", "QUARTERS", "ENGINEERING" };
    private static readonly (float z0, float z1)[] Rooms = { (-13f, -6f), (-6f, 0f), (0f, 5f), (5f, 9f), (9f, 13f) };
    private static readonly float[] Bulkheads = { -6f, 0f, 5f, 9f };   // bulkhead k sits between room k and k+1
    private const float MaxLevel = 1.3f;
    private static float RoomLen(int i) => Rooms[i].z1 - Rooms[i].z0;

    private sealed class Breach
    {
        public int Room;
        public Vector3 Pos, Normal;
        public float Severity, Weld;
        public GpuParticles3D Spray;
        public Interactable Use;
    }

    private readonly List<Breach> _breaches = new();
    private readonly float[] _level = new float[5];
    private readonly MeshInstance3D[] _water = new MeshInstance3D[5];
    private readonly MeshInstance3D[] _surface = new MeshInstance3D[5];
    private readonly ShaderMaterial[] _surfaceMat = new ShaderMaterial[5];
    private readonly float[] _turb = new float[5];
    private readonly OmniLight3D[] _alarm = new OmniLight3D[5];
    private readonly bool[] _sealed = new bool[4];
    private readonly StaticBody3D[] _hatch = new StaticBody3D[4];
    private readonly Interactable[] _hatchUse = new Interactable[4];
    private readonly List<GpuParticles3D> _drips = new();
    private readonly List<Vector3> _steamSpots = new();
    private readonly Random _rng = new();
    private float _t, _klaxonT, _dripSoundT = 2f, _steamCheck, _knockTimer, _stressCheck, _weldSoundT;
    private int _pendingKnocks;
    private float _knockGap;
    private Vector3 _knockFrom;

    public float Unease;     // story progress raises the knock rate
    public int ActiveBreaches => _breaches.Count;
    public System.Collections.Generic.IEnumerable<string> BreachRooms() { foreach (var b in _breaches) yield return RoomNames[b.Room]; }
    public float TotalFlood { get { float s = 0; foreach (var l in _level) s += l; return s; } }

    public override void _Ready()
    {
        var waterMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.04f, 0.14f, 0.15f, 0.62f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.05f, Metallic = 0.4f,
            EmissionEnabled = true, Emission = new Color(0.01f, 0.03f, 0.035f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var surfaceShader = GD.Load<Shader>("res://Shaders/flood_water.gdshader");
        for (int i = 0; i < 5; i++)
        {
            var (z0, z1) = Rooms[i];
            // Full room length: neighbouring volumes meet inside the bulkhead, so where a doorway is
            // open the surface simply continues through it (sealed hatches cover the seam).
            float w = SubInterior.HalfW * 2f - 0.02f, len = z1 - z0;
            // Body of water (darker, under the surface) + a subdivided, wave-animated surface on top.
            _water[i] = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(w, 1f, len) },
                MaterialOverride = waterMat, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0, 0, (z0 + z1) / 2f),
            };
            AddChild(_water[i]);
            _surfaceMat[i] = new ShaderMaterial { Shader = surfaceShader };
            _surfaceMat[i].SetShaderParameter("half_size", new Vector2(w / 2f, len / 2f));
            _surface[i] = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(w, len), SubdivideWidth = 20, SubdivideDepth = (int)(len * 5f) },
                MaterialOverride = _surfaceMat[i], Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0, 0, (z0 + z1) / 2f),
            };
            AddChild(_surface[i]);
            _alarm[i] = new OmniLight3D
            {
                Position = new Vector3(0, SubInterior.Height - 0.25f, (z0 + z1) / 2f),
                LightColor = new Color(1f, 0.08f, 0.05f), OmniRange = 7f, LightEnergy = 0f,
            };
            AddChild(_alarm[i]);
        }
        for (int k = 0; k < 4; k++) BuildHatch(k);
        BuildDrips();
        BuildTool();
        // Steam vents along the overhead pipes (see SubInterior.BuildPipes).
        for (float z = -11f; z < 12f; z += 2.8f)
            _steamSpots.Add(new Vector3(_rng.NextDouble() < 0.5 ? -1.55f : 1.6f, 2.42f, z + (float)_rng.NextDouble()));
        ResetKnockTimer();
    }

    // ------------------------------------------------------------------ hatches

    private void BuildHatch(int k)
    {
        float z = Bulkheads[k];
        var body = new StaticBody3D { CollisionLayer = 0, CollisionMask = 0, Position = new Vector3(0, 1.0f, z), Visible = false };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.1f, 2.0f, 0.12f) } });
        var mb = new MeshBuilder();
        mb.Box(Vector3.Zero, new Vector3(1.1f, 2.0f, 0.1f), new Color(0.3f, 0.32f, 0.3f));
        mb.Cylinder(new Vector3(0, 0, -0.07f), 0.22f, 0.22f, 0.04f, 8, new Color(0.7f, 0.12f, 0.08f));
        body.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Machine) });
        AddChild(body);
        _hatch[k] = body;
        // The interaction sits on the swung-open door leaf beside the doorway (see SubInterior.Bulkhead).
        _hatchUse[k] = Interactable.Create(this, new Vector3(1.15f, 1.0f, z - 0.2f), new Vector3(0.9f, 1.8f, 0.18f), "Seal hatch", () => ToggleHatch(k));
    }

    private void ToggleHatch(int k)
    {
        _sealed[k] = !_sealed[k];
        var h = _hatch[k];
        h.Visible = _sealed[k];
        h.CollisionLayer = _sealed[k] ? Layers.Interior | Layers.Use : 0;
        // When sealed, the hatch itself is the thing you open again.
        if (_sealed[k])
        {
            var it = Interactable.Create(h, Vector3.Zero, new Vector3(1.1f, 2.0f, 0.2f), "Open hatch", () => ToggleHatch(k));
            it.Name = "OpenUse";
        }
        else foreach (var c in h.GetChildren()) if (c is Interactable i && i.Name == "OpenUse") i.QueueFree();
        _hatchUse[k].Prompt = _sealed[k] ? "Hatch sealed" : "Seal hatch";
        Sound?.Invoke("door", ToGlobal(h.Position));
        Audio?.PlayImpact(0.4f);
        if (!_sealed[k] && Mathf.Abs(_level[k] - _level[k + 1]) > 0.2f) Message?.Invoke("The water finds its level with a sucking roar.");
    }

    // ------------------------------------------------------------------ breaches

    /// <summary>Open a new breach. room = -1 picks one at random (weighted toward the bow on impacts).</summary>
    public void OpenBreach(int room = -1, float severity = -1f)
    {
        if (_breaches.Count >= 3) return;
        if (room < 0) room = _rng.Next(5);
        if (severity < 0f) severity = 0.4f + (float)_rng.NextDouble() * 0.6f;
        var (z0, z1) = Rooms[room];
        float side = _rng.NextDouble() < 0.5 ? -1f : 1f;
        var pos = new Vector3(side * (SubInterior.HalfW - 0.05f), 0.6f + (float)_rng.NextDouble() * 1.2f, z0 + 0.6f + (float)_rng.NextDouble() * (z1 - z0 - 1.2f));
        var normal = new Vector3(-side, 0, 0);

        var spray = MakeSpray(severity);
        spray.Position = pos;
        spray.Basis = Basis.LookingAt(normal, Vector3.Up);
        AddChild(spray);

        var b = new Breach { Room = room, Pos = pos, Normal = normal, Severity = severity, Spray = spray };
        b.Use = Interactable.Create(this, pos + normal * 0.15f, new Vector3(0.4f, 0.6f, 0.6f), "Hold E: weld the breach (multi-tool + sealant or patch kit)", null);
        // A ragged hole in the wall plating.
        var mb = new MeshBuilder();
        mb.Rock(Vector3.Zero, new Vector3(0.05f, 0.22f, 0.22f), new Color(0.05f, 0.05f, 0.05f), _rng.Next(), 0.5f);
        b.Use.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.WallDark), Position = -normal * 0.1f });
        _breaches.Add(b);

        Sound?.Invoke("breach", ToGlobal(pos));
        Audio?.PlayImpact(1.2f);
        Player?.AddShake(0.6f);
        Message?.Invoke($"!! HULL BREACH — {RoomNames[room]} !!");
        _klaxonT = 0f;
    }

    private static GpuParticles3D MakeSpray(float severity)
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0.15f, -1f),     // -Z is "out of the wall" after LookingAt
            Spread = 7f + severity * 5f,
            InitialVelocityMin = 4f + severity * 2f,
            InitialVelocityMax = 6.5f + severity * 3f,
            Gravity = new Vector3(0, -9.8f, 0),
            ScaleMin = 0.4f, ScaleMax = 1.0f,
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Disabled,
        };
        return new GpuParticles3D
        {
            Amount = (int)(220 + severity * 260),
            Lifetime = 0.6f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.016f, 0.016f),
                Material = new StandardMaterial3D
                {
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(0.78f, 0.9f, 0.95f, 0.55f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            },
            VisibilityAabb = new Aabb(new Vector3(-3, -3, -4), new Vector3(6, 5, 6)),
        };
    }

    // ------------------------------------------------------------------ the multi-tool

    private Interactable _benchTool;
    private Node3D _handTool;
    private GpuParticles3D _sparks;
    private OmniLight3D _arc;
    private bool _welding;
    private Vector3 _weldAt;

    private static ArrayMesh ToolMesh()
    {
        var mb = new MeshBuilder();
        var body = new Color(0.75f, 0.55f, 0.12f);
        var dark = new Color(0.15f, 0.15f, 0.16f);
        mb.Box(new Vector3(0, 0, 0), new Vector3(0.06f, 0.08f, 0.2f), body);                    // housing
        mb.Box(new Vector3(0, -0.07f, 0.05f), new Vector3(0.045f, 0.1f, 0.05f), dark);          // grip
        mb.Box(new Vector3(0, 0.01f, -0.14f), new Vector3(0.022f, 0.022f, 0.09f), dark);        // nozzle
        mb.Box(new Vector3(0, 0.01f, -0.19f), new Vector3(0.012f, 0.012f, 0.012f), new Color(0.5f, 0.8f, 1f, 0.4f)); // tip
        mb.Box(new Vector3(0.032f, 0.02f, 0.02f), new Vector3(0.006f, 0.02f, 0.04f), new Color(0.3f, 1f, 0.4f, 0.4f)); // status lamp
        return mb.Build(Art.Machine);
    }

    private void BuildTool()
    {
        // On the mess table in the galley, until you take it.
        _benchTool = Interactable.Create(this, new Vector3(-1.75f, 0.82f, 2.95f), new Vector3(0.35f, 0.2f, 0.35f), "Take the multi-tool", () =>
        {
            Systems.Collect(ItemId.MultiTool, announce: false);
            Message?.Invoke("Multi-tool: arc welder, cutter, pry bar. With sealant or a patch kit it closes hull breaches.");
            Sound?.Invoke("pickup", _benchTool.GlobalPosition);
        });
        _benchTool.AddChild(new MeshInstance3D { Mesh = ToolMesh(), Rotation = new Vector3(Mathf.Pi / 2f, 0.6f, 0), Position = new Vector3(0, -0.02f, 0) });

        // In your hand while you weld.
        _handTool = new Node3D { Position = new Vector3(0.16f, -0.16f, -0.32f), Visible = false };
        _handTool.AddChild(new MeshInstance3D { Mesh = ToolMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _sparks = new GpuParticles3D
        {
            Position = new Vector3(0, 0.01f, -0.2f), Amount = 60, Lifetime = 0.45f, Emitting = false,
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = new Vector3(0, 0.4f, 0.6f), Spread = 70f, InitialVelocityMin = 1.2f, InitialVelocityMax = 3.2f,
                Gravity = new Vector3(0, -6f, 0), ScaleMin = 0.4f, ScaleMax = 1f,
            },
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.012f, 0.012f),
                Material = new StandardMaterial3D
                {
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(1f, 0.85f, 0.5f), EmissionEnabled = true, Emission = new Color(1f, 0.7f, 0.3f), EmissionEnergyMultiplier = 4f,
                },
            },
            LocalCoords = false,
            VisibilityAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)),
        };
        _handTool.AddChild(_sparks);
        _arc = new OmniLight3D { Position = new Vector3(0, 0.01f, -0.25f), LightColor = new Color(0.65f, 0.85f, 1f), OmniRange = 4f, LightEnergy = 0f };
        _handTool.AddChild(_arc);
        Player.Camera.AddChild(_handTool);
    }

    private void UpdateTool()
    {
        bool has = Systems.Hold.Count(ItemId.MultiTool) > 0;
        _benchTool.Visible = !has;
        _benchTool.CollisionLayer = has ? 0u : Layers.Use;
        _handTool.Visible = _welding;
        _sparks.Emitting = _welding;
        // The arc flickers violently: a weld is the brightest thing on the boat.
        _arc.LightEnergy = _welding ? 1.5f + 2.5f * (float)_rng.NextDouble() : 0f;
        if (_welding) _handTool.Position = new Vector3(0.16f + (float)(_rng.NextDouble() - 0.5) * 0.004f, -0.16f, -0.32f);
    }

    private void UpdateWelding(float dt)
    {
        _welding = false;
        foreach (var b in _breaches.ToArray())
        {
            bool welding = Player != null && Player.Focused == b.Use && Input.IsActionPressed(GameInput.Interact);
            if (!welding) { b.Weld = Mathf.Max(0f, b.Weld - dt * 0.3f); continue; }
            if (b.Weld <= 0.001f && Systems.Hold.Count(ItemId.MultiTool) == 0)
            {
                Message?.Invoke("You need the multi-tool to weld. It's on the mess table in the galley.");
                continue;
            }
            if (b.Weld <= 0.001f && !HasWeldStock())
            {
                Message?.Invoke("Nothing to weld with: you need sealant or a patch kit. Seal the hatch and get out.");
                continue;
            }
            _welding = true;
            _weldAt = ToGlobal(b.Pos);
            b.Weld += dt / 2.5f;
            _weldSoundT -= dt;
            if (_weldSoundT <= 0f) { Sound?.Invoke("weld", ToGlobal(b.Pos)); _weldSoundT = 0.22f; }
            Player.AddShake(0.02f);
            b.Use.Prompt = $"Welding... {Mathf.RoundToInt(b.Weld * 100)}%";
            if (b.Weld >= 1f)
            {
                if (!Systems.Hold.Remove(ItemId.Sealant)) Systems.Hold.Remove(ItemId.PatchKit);
                b.Spray.Emitting = false;
                var spray = b.Spray;
                GetTree().CreateTimer(1.0).Timeout += spray.QueueFree;
                b.Use.QueueFree();
                _breaches.Remove(b);
                Sub.Repair(4f);
                Audio?.PlayImpact(0.5f);
                Message?.Invoke(_breaches.Count == 0 ? "Breach welded. Bilge pumps running." : "Breach welded. There are more.");
            }
        }
    }

    private bool HasWeldStock() => Systems.Hold.Count(ItemId.Sealant) > 0 || Systems.Hold.Count(ItemId.PatchKit) > 0;

    // ------------------------------------------------------------------ water

    private void UpdateWater(float dt)
    {
        // Water is tracked as volume per room (level × room length; the width is the same everywhere),
        // so spreading through the boat conserves it. (Tom: "it would flow to other segments since
        // we don't have doors between segments" — the doorways are open, so it does.)

        // Inflow from breaches (m² of cross-section per second: a big breach floods a room in
        // ~30 s on its own, or the whole boat in a couple of minutes).
        foreach (var b in _breaches) _level[b.Room] += b.Severity * 0.25f / RoomLen(b.Room) * dt;

        // Flow through every open doorway, at any depth: levels equalise within a second or two.
        for (int iter = 0; iter < 2; iter++)
            for (int k = 0; k < 4; k++)
            {
                if (_sealed[k]) continue;
                float la = _level[k], lb = _level[k + 1];
                if (Mathf.Abs(la - lb) < 0.0005f) continue;
                float lenA = RoomLen(k), lenB = RoomLen(k + 1);
                float target = (la * lenA + lb * lenB) / (lenA + lenB);  // shared level, same volume
                float f = Mathf.Clamp(2.5f * dt, 0f, 1f);
                _level[k] = Mathf.Lerp(la, target, f);
                _level[k + 1] = Mathf.Lerp(lb, target, f);
            }

        // Bilge pumps take a fixed volume from the boat (if there's power), wettest rooms first.
        // A small breach they outpace; a big one they can't.
        if (!Systems.Emergency)
        {
            float pump = PumpRate * dt, used = 0f;
            for (int pass = 0; pass < 5 && pump > 1e-6f; pass++)
            {
                int wettest = -1;
                for (int i = 0; i < 5; i++) if (_level[i] > 0.001f && (wettest < 0 || _level[i] > _level[wettest])) wettest = i;
                if (wettest < 0) break;
                float take = Mathf.Min(pump, _level[wettest] * RoomLen(wettest));
                _level[wettest] -= take / RoomLen(wettest);
                pump -= take; used += take;
            }
            if (used > 0f) Systems.UsePower(used * 2.0f);
        }

        for (int i = 0; i < 5; i++)
        {
            _level[i] = Mathf.Clamp(_level[i], 0f, MaxLevel);

            var w = _water[i];
            bool wet = _level[i] > 0.01f;
            w.Visible = wet;
            _surface[i].Visible = wet;
            if (wet)
            {
                w.Scale = new Vector3(1, Mathf.Max(0.001f, _level[i] - 0.015f), 1);
                w.Position = new Vector3(0, (_level[i] - 0.015f) / 2f, w.Position.Z);
                _surface[i].Position = new Vector3(0, _level[i], _surface[i].Position.Z);
                // Churn eases in and out with the inflow; ripples centre where the spray lands.
                var pour = _breaches.Find(b => b.Room == i);
                _turb[i] = Mathf.MoveToward(_turb[i], pour != null ? pour.Severity : 0f, dt * 0.6f);
                _surfaceMat[i].SetShaderParameter("turbulence", _turb[i]);
                if (pour != null)
                {
                    float roomZ = (Rooms[i].z0 + Rooms[i].z1) / 2f;
                    var land = pour.Pos + pour.Normal * 1.2f;
                    _surfaceMat[i].SetShaderParameter("inflow", new Vector2(land.X, land.Z - roomZ));
                }
            }
            // Alarm lights flash in breached rooms.
            bool alarm = _breaches.Exists(b => b.Room == i);
            _alarm[i].LightEnergy = alarm && Mathf.PosMod(_t, 1.0f) < 0.5f ? 1.6f : 0f;
        }

        // The water is heavy: the boat sinks if it floods (ballast can fight it for a while).
        Sub.FloodWeight = TotalFlood * 22f;

        // Wading slows the captain.
        if (Player != null)
        {
            float pz = Player.Position.Z;
            float lvl = 0f;
            for (int i = 0; i < 5; i++) if (pz >= Rooms[i].z0 && pz < Rooms[i].z1) lvl = _level[i];
            Player.WaterFactor = 1f - Mathf.Clamp(lvl / MaxLevel, 0f, 1f) * 0.55f;
        }

        float spray = 0f;
        foreach (var b in _breaches) spray = Mathf.Max(spray, b.Severity);
        if (Audio != null) Audio.Spray = spray;

        // Klaxon while anything is open.
        if (_breaches.Count > 0)
        {
            _klaxonT -= dt;
            if (_klaxonT <= 0f) { Audio?.PlayAt("klaxon", 0f, 0.8f); _klaxonT = 6f; }
        }
    }

    // ------------------------------------------------------------------ steam & drips

    private void BuildDrips()
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = Vector3.Down, Spread = 2f,
            InitialVelocityMin = 0.2f, InitialVelocityMax = 0.4f,
            Gravity = new Vector3(0, -9.8f, 0),
        };
        var mesh = new QuadMesh
        {
            Size = new Vector2(0.018f, 0.045f),
            Material = new StandardMaterial3D
            {
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.7f, 0.85f, 0.9f, 0.75f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        for (int i = 0; i < 10; i++)
        {
            var p = new GpuParticles3D
            {
                Amount = 2, Lifetime = 0.8f, Emitting = false,
                ProcessMaterial = pm, DrawPass1 = mesh,
                Position = new Vector3((float)(_rng.NextDouble() * 3.6 - 1.8), SubInterior.Height - 0.05f, -11.5f + i * 2.5f + (float)_rng.NextDouble()),
                VisibilityAabb = new Aabb(new Vector3(-0.5f, -3f, -0.5f), new Vector3(1f, 3.2f, 1f)),
                SpeedScale = 0.6f + (float)_rng.NextDouble() * 0.6f,
            };
            AddChild(p);
            _drips.Add(p);
        }
    }

    private void UpdateDrips(float dt)
    {
        // More drips the worse things get.
        float damage = 1f - Sub.Hull / 100f;
        int active = Mathf.Clamp((int)(damage * 9f + Sub.Stress * 5f + _breaches.Count * 2f), 0, _drips.Count);
        for (int i = 0; i < _drips.Count; i++) _drips[i].Emitting = i < active;
        if (active == 0) return;
        _dripSoundT -= dt;
        if (_dripSoundT <= 0f)
        {
            var d = _drips[_rng.Next(active)];
            Sound?.Invoke("drip", ToGlobal(d.Position - Vector3.Up * SubInterior.Height));
            _dripSoundT = 0.8f + (float)_rng.NextDouble() * 2.5f / Mathf.Max(1, active * 0.5f);
        }
    }

    private void UpdateSteam(float dt)
    {
        _steamCheck -= dt;
        if (_steamCheck > 0f) return;
        _steamCheck = 1f;
        float chance = 0.012f + Sub.Stress * 0.06f + (1f - Sub.Hull / 100f) * 0.05f + (Mathf.Abs(Sub.EngineOutput) > 0.8f ? 0.015f : 0f);
        if (_rng.NextDouble() < chance) Vent(_steamSpots[_rng.Next(_steamSpots.Count)]);
    }

    /// <summary>A burst of steam from a pipe joint.</summary>
    public void Vent(Vector3 at)
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, -0.6f, 0), Spread = 35f,
            InitialVelocityMin = 1.2f, InitialVelocityMax = 2.4f,
            Gravity = new Vector3(0, 0.35f, 0),
            DampingMin = 1.5f, DampingMax = 2.5f,
            ScaleMin = 0.6f, ScaleMax = 1.6f,
            ScaleCurve = new CurveTexture { Curve = Grow() },
            Color = new Color(1, 1, 1, 0.3f),
            ColorRamp = new GradientTexture1D { Gradient = new Gradient { Colors = new[] { new Color(0.85f, 0.88f, 0.88f, 0.4f), new Color(0.85f, 0.88f, 0.88f, 0f) } } },
        };
        var p = new GpuParticles3D
        {
            Amount = 36, Lifetime = 1.8f, OneShot = true, Explosiveness = 0.55f, Position = at,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.35f, 0.35f),
                Material = new StandardMaterial3D
                {
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor = Colors.White,
                },
            },
            VisibilityAabb = new Aabb(new Vector3(-2, -2.5f, -2), new Vector3(4, 3.5f, 4)),
        };
        AddChild(p);
        p.Emitting = true;
        GetTree().CreateTimer(2.5).Timeout += p.QueueFree;
        Sound?.Invoke("steam", ToGlobal(at));
    }

    private static Curve Grow()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.3f));
        c.AddPoint(new Vector2(1, 1.6f));
        return c;
    }

    // ------------------------------------------------------------------ knocks

    private void ResetKnockTimer() => _knockTimer = (240f + (float)_rng.NextDouble() * 360f) * (1f - 0.5f * Unease);

    /// <summary>Three knocks from one side of the hull. Sometimes a fourth, much later, from the other.</summary>
    public void Knock()
    {
        float a = (float)_rng.NextDouble() * Mathf.Tau;
        _knockFrom = new Vector3(Mathf.Cos(a) * 3.2f, 1.3f, -8f + (float)_rng.NextDouble() * 18f);
        _pendingKnocks = 3;
        _knockGap = 0f;
    }

    private void UpdateKnocks(float dt)
    {
        if (_pendingKnocks > 0)
        {
            _knockGap -= dt;
            if (_knockGap <= 0f)
            {
                Sound?.Invoke("knock", ToGlobal(_knockFrom));
                Player?.AddShake(0.12f);
                _pendingKnocks--;
                _knockGap = _pendingKnocks > 0 ? 0.78f : 0f;
                if (_pendingKnocks == 0 && _rng.NextDouble() < 0.3)
                {
                    // ...and one more, later, from the other side.
                    var other = new Vector3(-_knockFrom.X, 1.3f, _knockFrom.Z + 4f);
                    GetTree().CreateTimer(9.0 + _rng.NextDouble() * 8.0).Timeout += () =>
                    {
                        if (Aboard?.Invoke() == true) { Sound?.Invoke("knock", ToGlobal(other)); Player?.AddShake(0.1f); }
                    };
                }
            }
            return;
        }
        bool quietDeepAlone = Aboard?.Invoke() == true && Docked?.Invoke() != true && Sub.Depth > 300f && Sub.LinearVelocity.Length() < 1.2f;
        if (!quietDeepAlone) return;
        _knockTimer -= dt;
        if (_knockTimer <= 0f) { Knock(); ResetKnockTimer(); }
    }

    // ------------------------------------------------------------------ triggers

    /// <summary>Modules: ablative skin makes breaches rarer; bilge pumps drain faster (m²/s).</summary>
    public float BreachScale = 1f, PumpRate = 0.09f;

    public void OnImpact(float strength)
    {
        if (strength > 1.4f && _rng.NextDouble() < (strength - 1.2f) * 0.45f * BreachScale)
            OpenBreach(_rng.NextDouble() < 0.5 ? 0 : -1, Mathf.Clamp(strength * 0.35f, 0.4f, 1f));
        else if (strength > 0.8f && _rng.NextDouble() < 0.5) Vent(_steamSpots[_rng.Next(_steamSpots.Count)]);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        if (Sub == null) return;

        // Pressure and damage: breaches open on their own when you push too deep or too hurt.
        _stressCheck -= dt;
        if (_stressCheck <= 0f)
        {
            _stressCheck = 1f;
            float p = Sub.Stress * 0.012f + (Sub.Hull < 35f ? 0.004f : 0f);
            if (Docked?.Invoke() != true && _rng.NextDouble() < p) OpenBreach();
        }

        UpdateWelding(dt);
        UpdateTool();
        UpdateWater(dt);
        UpdateSteam(dt);
        UpdateDrips(dt);
        UpdateKnocks(dt);
    }

    /// <summary>Dev/capture: where the first open breach is (interior-local) and which way it faces.</summary>
    public (Vector3 pos, Vector3 normal, int room)? FirstBreach => _breaches.Count > 0 ? (_breaches[0].Pos, _breaches[0].Normal, _breaches[0].Room) : null;
    public float Level(int room) => _level[room];

    /// <summary>HUD warning line, or null.</summary>
    public string Warning()
    {
        if (_breaches.Count > 0) return $"!! HULL BREACH — {RoomNames[_breaches[0].Room]}{(_breaches.Count > 1 ? $" (+{_breaches.Count - 1})" : "")} !!";
        if (TotalFlood > 0.3f) return "FLOODING — BILGE PUMPS RUNNING";
        return null;
    }
}
