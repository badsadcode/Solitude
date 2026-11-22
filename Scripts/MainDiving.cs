using Godot;
using Solitude.Core;
using Solitude.Player;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// Milestone 5 glue: the dive locker, the hatch interlock, going outside in the hard suit,
/// collecting by hand, the swim home, and what happens when the air runs out.
/// </summary>
public partial class Main
{
    private Diver _diver;
    private Harpoon _harpoon;
    private float _zap;
    private bool _suitOn;
    private OmniLight3D _hatchLight;
    private float _hatchBlink;

    /// <summary>Hatch on the sub's back, in sub space (see SubmarineModel: dive hatch at z = 9).</summary>
    private static readonly Vector3 HatchLocal = new(0, 3.3f, 9f);
    private const float HatchReach = 3.2f;

    private void SetupDiving()
    {
        _diver = new Diver { Name = "Diver", Player = _player, Terrain = _terrain };
        _game.AddChild(_diver);
        _diver.OxygenWarning += () =>
        {
            _hud.Message("LOW AIR. Get back to the hatch.", 5f);
            _audio.PlayAt("deny", 0f, 0.8f);
        };
        _ravager.DiverTimeOutside = () => _diver.TimeOutside;
        _ravager.Bite += dmg =>
        {
            if (!_diver.Active) return;
            _player.Damage(dmg);
            _diver.Velocity += (_diver.GlobalPosition - _ravager.GlobalPosition).Normalized() * 6f;
            _audio.PlayImpact(1.5f);
            _hud.Message("Something hit you very hard.", 4f);
        };
        _harpoon = new Harpoon
        {
            Name = "Harpoon", Diver = _diver,
            Targets = HarpoonTargets,
            Sound = (k, p) => { PlaySpatial(k, p); if (k is "harpoon" or "shock") _listener.AddPulse(0.2f); },
            Message = m => _hud.Message(m, 3f),
            SelfShock = dmg => { _player.Damage(dmg); _zap = 1f; _audio.PlayAt("hurt", 0f, 0.5f); },
        };
        _game.AddChild(_harpoon);
        _hatchLight = new OmniLight3D { Position = HatchLocal + Vector3.Up * 0.6f, LightColor = new Color(1f, 0.7f, 0.25f), OmniRange = 7f, LightEnergy = 0f };
        _sub.AddChild(_hatchLight);
    }

    private System.Collections.Generic.IEnumerable<Creatures.IHarpoonable> HarpoonTargets()
    {
        if (_ravager != null && _ravager.IsInsideTree() && _ravager.Visible) yield return _ravager;
        foreach (var r in _creatures.Rays) if (r.IsInsideTree()) yield return r;
        if (_gloam != null && _gloam.IsInsideTree()) yield return _gloam;
        if (_creatures.Wyrm is { } w && w.IsInsideTree()) yield return w;
    }

    /// <summary>Focus for terrain streaming and marine snow: the diver while outside, else the boat.</summary>
    private Vector3 FocusPoint() => _diver != null && _diver.Active ? _diver.GlobalPosition : _sub.GlobalPosition;

    // ------------------------------------------------------------------ the dive locker

    private void OnSuit()
    {
        if (_player.InStation) return;
        _suitOn = !_suitOn;
        _audio.PlayImpact(0.3f);
        _audio.PlayAt("airlock", 0f, 0.25f);
        _hud.Message(_suitOn
            ? "Hard suit sealed. Four minutes of air. The dive hatch is up the ladder."
            : "Suit off and stowed. It smells of copper and your own fear.", 5f);
        _player.SpeedFactor = _suitOn ? 0.7f : 1f;
    }

    // ------------------------------------------------------------------ out and back

    private bool TryDiveFromHatch()
    {
        if (!_suitOn) { _hud.Message("Hatch opens onto 500 m of water. Suit up first (dive locker, port side)."); _audio.PlayAt("deny", 0f, 0.6f); return false; }
        if (_sub.LinearVelocity.Length() > 0.6f || Mathf.Abs(_sub.EngineOutput) > 0.05f)
        {
            _hud.Message("HATCH INTERLOCK: the boat must be stopped (ALL STOP and drifting under 0.6 m/s).");
            _audio.PlayAt("deny", 0f, 0.6f);
            return false;
        }
        _player.InputEnabled = false;
        _audio.PlayAt("airlock", 0f, 1f);
        _hud.FadeTo(1f, 0.8f);
        var tw = CreateTween();
        tw.TweenInterval(1.8);
        tw.TweenCallback(Callable.From(EnterDive));
        return true;
    }

    private void EnterDive()
    {
        _mode = Mode.Diving;
        Vector3 hatch = _sub.GlobalTransform * HatchLocal;
        Vector3 f = -_sub.GlobalBasis.Z; f.Y = 0; f = f.Normalized();
        _diver.SetActive(true);
        _diver.Place(hatch + Vector3.Up * 1.4f + f * 0.5f, Mathf.Atan2(-f.X, -f.Z));
        _diver.Camera.MakeCurrent();
        _diver.Lamp.Visible = true;
        _harpoon.SetActive(true);
        _ravager.Diver = _diver;
        _rig.MonitorsActive = false;
        _rig.BridgeActive = false;
        _audio.Inside = false;
        _audio.Diving = true;
        _hud.ShowHelmet(true);
        _hud.SetCrosshair(false);
        _hud.SetHelp("WASD swim · SPACE/C up/down · SHIFT boost · F lamp · E collect/airlock · LMB fire · Q spear/prod");
        _hud.FadeTo(0f, 1.5f);
        _hud.Message("Outside. The light stops a few metres from your face.", 5f);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void UpdateDiving(float dt)
    {
        if (!_diver.Active) return;
        Vector3 hatch = _sub.GlobalTransform * HatchLocal;
        float toHatch = _diver.GlobalPosition.DistanceTo(hatch);

        string prompt = "";
        if (toHatch < HatchReach) prompt = "[E] Enter the airlock";
        else if (_diver.Nearest != null) prompt = $"[E] Collect {Items.Name(_diver.Nearest.Type)}";
        else if (_diver.Bag.Count >= Diver.BagSize) prompt = "Sample bag full";
        _hud.SetPrompt(prompt);

        if (Input.IsActionJustPressed(GameInput.Interact))
        {
            if (toHatch < HatchReach) { ExitDive(); return; }
            if (_diver.Collect() is ItemId got)
            {
                _audio.PlayAt("pickup", 0f, 0.5f);
                _hud.Message($"Bagged: {Items.Name(got)} ({_diver.Bag.Count}/{Diver.BagSize}).", 3f);
            }
        }
        if (Input.IsActionJustPressed(GameInput.Flashlight) && (_diver.Lamp.Visible || !LampLocked())) { _diver.Lamp.Visible = !_diver.Lamp.Visible; _audio.PlayAt("empty", 0.3f, 0.6f); }

        // Helmet readouts
        var h = _hud.Helmet;
        h.Oxygen = _diver.Oxygen;
        h.Depth = Mathf.Max(0f, -_diver.GlobalPosition.Y);
        h.Bag = _diver.Bag.Count;
        h.Lamp = _diver.Lamp.Visible;
        h.HomeDistance = toHatch;
        Vector3 rel = _diver.Camera.GlobalBasis.Inverse() * (hatch - _diver.Camera.GlobalPosition);
        h.HomeBearing = Mathf.RadToDeg(Mathf.Atan2(rel.X, -rel.Z));
        Vector3 aim = _harpoon.AimPoint;
        h.ReticleOn = !_diver.Camera.IsPositionBehind(aim);
        h.Reticle = _diver.Camera.UnprojectPosition(aim) * PixelScale;
        h.Weapon = _harpoon.Status;
        h.WeaponReady = _harpoon.Current == Harpoon.Mode.Spear ? _harpoon.SpearReady : _harpoon.Charge >= 1f;
        _zap = Mathf.MoveToward(_zap, 0f, dt * 2.5f);
        h.ZapFlash = _zap;

        // Panic: low air, or something enormous close by.
        float air = Mathf.Clamp((35f - _diver.Oxygen) / 35f, 0f, 1f);
        float near = Mathf.Max(Mathf.Clamp((40f - _ravager.DistanceToDiver) / 30f, 0f, 1f),
                               Mathf.Max(Mathf.Clamp((90f - _creatures.ApexDistance) / 60f, 0f, 1f),   // the wyrm is felt from further away
                                         Mathf.Clamp((70f - GloamDistance) / 50f, 0f, 1f)));
        float panic = Mathf.Max(air, near);
        _audio.Heartbeat = panic;
        _audio.Exertion = _diver.Exertion;
        h.Fog = Mathf.Max(panic * 0.8f, _diver.Oxygen <= 0f ? 1f : 0f);
        _post.SetShaderParameter("wobble", 1.2f + panic);
    }

    private void ExitDive()
    {
        _diver.SetPhysicsProcess(false);
        _audio.PlayAt("airlock", 0f, 1f);
        _hud.FadeTo(1f, 0.8f);
        var tw = CreateTween();
        tw.TweenInterval(1.8);
        tw.TweenCallback(Callable.From(() =>
        {
            int stowed = 0;
            foreach (var item in _diver.Bag.ToArray())
                if (_systems.Collect(item, announce: false)) { _diver.Bag.Remove(item); stowed++; }
            EndDive();
            _player.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), SubHatchSpot);
            _hud.FadeTo(0f, 1.2f);
            _hud.Message(stowed > 0 ? $"Airlock drained. {stowed} sample{(stowed > 1 ? "s" : "")} stowed in the hold." + (_diver.Bag.Count > 0 ? " The hold is full; the rest stay in the bag." : "") : "Airlock drained. You're back. Breathe.", 5f);
            Autosave("back from the dive");
        }));
    }

    /// <summary>Put everything back to "aboard the boat" (also used when you die out there).</summary>
    private void EndDive()
    {
        _diver.SetActive(false);
        _harpoon.SetActive(false);
        _ravager.Diver = null;
        _audio.Diving = false;
        _audio.Heartbeat = 0f;
        _audio.Exertion = 0f;
        _hud.ShowHelmet(false);
        _player.Velocity = Vector3.Zero;
        EnterWalking();
    }

    private void UpdateHatchBeacon(float dt)
    {
        // A slow amber blink on the hatch whenever someone is outside, so you can find home.
        _hatchBlink += dt;
        _hatchLight.LightEnergy = _diver.Active && Mathf.PosMod(_hatchBlink, 1.6f) < 0.5f ? 3f : 0f;
    }
}
