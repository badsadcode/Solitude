using System;
using Godot;
using Solitude.Audio;
using Solitude.Sub;
using Solitude.World;

namespace Solitude.Systems;

/// <summary>
/// The boat's survival state: battery, the captain's hunger, the hold, and the intake filter.
///
/// Power (0..100): drains from life support, engine output, floodlights, the intake, the arm
/// and per-use costs (pings, crafting). Recharges from installed power cells and, slowly,
/// from the thermal skin when parked near a hydrothermal vent. At zero the boat is on
/// emergency power: engine limited, floods and sonar dead.
/// Hunger (100 = fed .. 0 = starving): falls over ~25 minutes; eat at the galley.
/// </summary>
public partial class ShipSystems : Node
{
    public Submarine Sub;
    public TerrainStreamer Terrain;
    public TerrainGenerator Gen;
    public AudioDirector Audio;
    public Action<string> Message;

    public readonly Inventory Hold = new();

    public float Power { get; private set; } = 100f;
    /// <summary>Battery bank size relative to stock (the Battery upgrade makes every drain last longer).</summary>
    public float BatteryCapacity = 1f;
    public float Food { get; private set; } = 80f;
    public bool IntakeOn { get; private set; }
    public bool ArmActive;                 // set by the arm while deployed
    public bool Emergency => Power <= 0.01f;
    public bool Thermal { get; private set; }

    // Fractional progress of intake collection.
    private float _plankton, _sediment;
    private float _warnTimer;
    private bool _warnedLowPower, _warnedHunger;

    // Drain rates (% per second)
    private const float LifeSupport = 0.015f, EnginePerUnit = 0.11f, Floods = 0.025f, IntakeDrain = 0.03f, ArmDrain = 0.05f;
    private const float FoodPerSecond = 100f / (25f * 60f);

    public override void _Ready()
    {
        Hold.Add(ItemId.Ration, 2);
        Hold.Add(ItemId.PatchKit, 1);
        Hold.Add(ItemId.PowerCell, 1);
    }

    /// <summary>Restore from a save.</summary>
    public void SetState(float power, float food) { Power = power; Food = food; }

    public bool UsePower(float amount)
    {
        if (Power < amount) return false;
        Power -= amount;
        return true;
    }

    public void ToggleIntake()
    {
        IntakeOn = !IntakeOn && !Emergency;
        Message?.Invoke(IntakeOn ? "INTAKE FILTER: open — collecting plankton (and sediment near the bottom)" : "INTAKE FILTER: closed");
    }

    public void InstallPowerCell()
    {
        if (!Hold.Remove(ItemId.PowerCell)) { Message?.Invoke("No power cell in the hold."); return; }
        Power = Mathf.Min(100f, Power + 40f);
        Audio?.PlayImpact(0.3f);
        Message?.Invoke("Power cell installed. Bus voltage recovering.");
    }

    public void ApplyPatch()
    {
        if (Sub.Hull >= 99.5f) { Message?.Invoke("Hull is sound. Save the kit."); return; }
        if (!Hold.Remove(ItemId.PatchKit)) { Message?.Invoke("No patch kit in the hold."); return; }
        Sub.Repair(30f);
        Audio?.PlayImpact(0.5f);
        Message?.Invoke("Patch welded and sealed. Hull integrity restored.");
    }

    public void Eat(ItemId food)
    {
        var info = Items.Info[food];
        if (info.Food <= 0f || !Hold.Remove(food)) return;
        Food = Mathf.Min(100f, Food + info.Food);
        Message?.Invoke($"You eat the {info.Name.ToLowerInvariant()}. {info.Description}");
    }

    public bool Craft(Recipe r)
    {
        if (!Hold.Has(r.Cost)) { Message?.Invoke("Missing ingredients."); return false; }
        if (Power < r.PowerCost) { Message?.Invoke("Not enough power."); return false; }
        // Weight check after consuming inputs.
        int freed = 0;
        foreach (var (id, n) in r.Cost) freed += Items.Info[id].Weight * n;
        if (Hold.Weight - freed + Items.Info[r.Output].Weight * r.OutputCount > Hold.Capacity) { Message?.Invoke("Hold is full."); return false; }
        foreach (var (id, n) in r.Cost) Hold.Remove(id, n);
        Hold.Add(r.Output, r.OutputCount);
        Power -= r.PowerCost;
        Message?.Invoke($"{r.Verb}: {Items.Name(r.Output)}.");
        return true;
    }

    /// <summary>Add a collected item, with feedback.</summary>
    public bool Collect(ItemId id, bool announce = true)
    {
        if (!Hold.Add(id)) { Message?.Invoke($"HOLD FULL — {Items.Name(id)} lost."); return false; }
        if (announce) Message?.Invoke($"Recovered: {Items.Name(id)}.");
        return true;
    }

    public override void _Process(double delta)
    {
        if (Sub == null) return;
        float dt = (float)delta;

        // --- power
        float drain = (LifeSupport + Mathf.Abs(Sub.EngineOutput) * EnginePerUnit) / BatteryCapacity;
        if (Sub.LightsOn) drain += Floods;
        if (IntakeOn) drain += IntakeDrain;
        if (ArmActive) drain += ArmDrain;
        Power = Mathf.Max(0f, Power - drain * dt);

        // Thermal recharge: slow and close to a vent.
        Thermal = false;
        if (Terrain != null && Mathf.Abs(Sub.ForwardSpeed) < 1.2f)
        {
            float d = Terrain.NearestVentDistance(Sub.GlobalPosition);
            if (d < 40f)
            {
                Thermal = true;
                Power = Mathf.Min(100f, Power + (0.35f * (1f - d / 40f) + 0.05f) * dt);
            }
        }

        // Emergency power: engine limited, lights and intake die.
        Sub.EngineLimit = Emergency ? 0.15f : 1f;
        if (Emergency)
        {
            if (Sub.LightsOn) { Sub.LightsOn = false; Message?.Invoke("!! POWER DEPLETED — emergency bus only. Floods offline."); }
            IntakeOn = false;
        }
        if (Power < 15f && !_warnedLowPower) { _warnedLowPower = true; Message?.Invoke("LOW POWER — install a power cell or rest near a vent."); Audio?.PlayBlip(300f, 0.25f, 0.6f); }
        if (Power > 25f) _warnedLowPower = false;

        // --- intake filter
        if (IntakeOn)
        {
            float speed = Mathf.Abs(Sub.ForwardSpeed);
            _plankton += dt * Mathf.Clamp(speed / 3f, 0f, 1f) / 12f;     // ~1 per 12 s at speed
            float alt = Gen != null ? Sub.GlobalPosition.Y - Gen.Height(Sub.GlobalPosition.X, Sub.GlobalPosition.Z) : 99f;
            if (alt < 6f && speed > 0.2f && speed < 2.5f) _sediment += dt / 10f;
            if (_plankton >= 1f) { _plankton -= 1f; Collect(ItemId.Plankton, announce: false); Audio?.PlayBlip(660f, 0.03f, 0.25f); }
            if (_sediment >= 1f) { _sediment -= 1f; Collect(ItemId.Sediment, announce: false); Audio?.PlayBlip(520f, 0.03f, 0.25f); }
        }

        // --- hunger
        Food = Mathf.Max(0f, Food - FoodPerSecond * dt);
        if (Food < 20f && !_warnedHunger) { _warnedHunger = true; Message?.Invoke("You are starving. Your hands shake on the controls."); }
        if (Food > 30f) _warnedHunger = false;
    }
}
