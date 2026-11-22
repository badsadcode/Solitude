using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Creatures;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// The sample lab's glue (see Systems/Lab.cs): the bench panel, the analysis ticking along in the
/// background, field notes into the journal, scraps from the harpoon, and what the findings change.
/// Kept deliberately quiet about the details: Tom plays this too.
/// </summary>
public partial class Main
{
    private readonly Lab _lab = new();
    private float _labScreenT;

    // The lure flare: one at a time, drifting with the current.
    private Node3D _lure;
    private OmniLight3D _lureLight;
    private float _lureLife;
    public const float LureLife = 90f;

    private void SetupLab()
    {
        SetupWrecks();
        SetupWear();
        SetupHanna();
        // The holo table marks distress calls you've heard, too.
        var beacons = _interior.Holo.Beacons;
        _interior.Holo.Beacons = () =>
        {
            var list = new List<Vector3>(beacons?.Invoke() ?? new List<Vector3>());
            list.AddRange(HeardWreckBeacons());
            return list;
        };
        _lab.Finished += OnStudyDone;
        _harpoon.SpearHit += OnSpearHit;
    }

    private void UpdateLab(float dt)
    {
        if (_mode != Mode.Title && !_ended)
        {
            bool powered = !_systems.Emergency;
            if (_lab.Running != null && powered) _systems.UsePower(0.012f * dt);
            _lab.Update(dt, powered);
        }
        _labScreenT -= dt;
        if (_labScreenT <= 0f)
        {
            _labScreenT = 0.5f;
            _interior.LabScreen.Text = _lab.Running is ItemId r
                ? $"{_lab.Progress * 100f:0}%{(_systems.Emergency ? " NO PWR" : "")}"
                : "IDLE";
        }
        UpdateLure(dt);
    }

    /// <summary>The bench panel (rebuilt by RefreshPanel). Returns the title.</summary>
    private string LabPanel(List<(string, bool)> opts)
    {
        var hold = _systems.Hold;
        if (_lab.Running is ItemId run)
            opts.Add(($"## UNDER THE SCOPE: {Items.Name(run)}  {_lab.Progress * 100f:0}%  (~{Mathf.CeilToInt(_lab.Left / 60f)} min)", false));
        else opts.Add(("## THE SCOPE IS FREE: pick a sample", false));
        _panelActions.Add(() => { });

        bool any = false;
        foreach (var st in Lab.All)
        {
            if (_lab.Done.Contains(st.Item) || hold.Count(st.Item) == 0) continue;
            any = true;
            var item = st.Item;
            opts.Add(($"Analyse {Items.Name(item)}  (~{st.Seconds / 60f:0.#} min, uses one)   x{hold.Count(item)}", _lab.Running == null));
            _panelActions.Add(() =>
            {
                if (_lab.Running != null || !hold.Remove(item)) return;
                _lab.Start(item);
                _audio.PlayBlip(1500f, 0.04f, 0.3f);
                Log($"LAB: {Items.Name(item)} under the scope.");
            });
        }
        if (!any)
        {
            opts.Add(("(nothing new in the hold to study: collect, and bring things back)", false));
            _panelActions.Add(() => { });
        }

        if (_lab.Has(Discovery.LureFlares))
        {
            var cost = new[] { (ItemId.Lumen, 1), (ItemId.Sealant, 1) };
            opts.Add(($"Prepare lure flare  <-  1x {Items.Name(ItemId.Lumen)} + 1x {Items.Name(ItemId.Sealant)}   (have {hold.Count(ItemId.LureFlare)})", hold.Has(cost)));
            _panelActions.Add(() =>
            {
                if (!hold.Has(cost)) return;
                hold.Remove(ItemId.Lumen); hold.Remove(ItemId.Sealant); hold.Add(ItemId.LureFlare);
                _audio.PlayBlip(700f, 0.1f, 0.4f);
            });
        }

        if (_lab.Done.Count > 0)
        {
            opts.Add(($"## STUDIED  {_lab.Done.Count}/{Lab.All.Length}  (field notes in the journal: J)", false));
            _panelActions.Add(() => { });
            foreach (var st in Lab.All)
            {
                if (!_lab.Done.Contains(st.Item)) continue;
                opts.Add(($"  {st.Title}", false));
                _panelActions.Add(() => { });
            }
        }
        return "LAB  —  analyse samples";
    }

    private void OnStudyDone(Lab.Study st)
    {
        string body = st.Note + (st.GivesText != null ? $"\n\n> {st.GivesText}" : "");
        _shipLog.AddNote(st.Title, body, "LAB · FIELD NOTES");
        Log($"LAB: analysis complete. {st.Title}.");
        _hud.Message($"LAB: {st.Title}. Field notes in the journal (J).", 7f);   // short on screen: the whole finding is in the journal
        _audio.PlayBlip(1200f, 0.08f, 0.5f);
        _audio.PlayBlip(1600f, 0.08f, 0.4f);
        ApplyDiscoveries();
        if (_mode == Mode.Panel && _panelKind == "lab") RefreshPanel();
        Autosave("lab");
    }

    private void ApplyDiscoveries()
    {
        _sonar.Recharge = _lab.Has(Discovery.FastSonar) ? 2.5f : 4f;
        _labIntakeYield = _lab.Has(Discovery.RichIntake) ? 1.6f : 1f;   // (the intake filter's condition multiplies this)
        Ravager.StunFactor = _lab.Has(Discovery.NerveMap) ? 2.5f : 1f;
        if (!_lab.Has(Discovery.RedFloods)) _sub.RedFloods = false;
    }

    // ------------------------------------------------------------------ scraps from the harpoon

    private void OnSpearHit(IHarpoonable c)
    {
        ItemId? scrap = c switch
        {
            Ravager => ItemId.TissueRavager,
            Gloam => ItemId.PlateGloam,
            Swimmer s when s.Profile.Name.Contains("Ray") => ItemId.TissueRay,
            Swimmer s when s.Profile.Name.Contains("Fangwyrm") => ItemId.ScaleWyrm,
            _ => null,
        };
        if (scrap is not ItemId id) return;
        // One of each is plenty: nothing more once it's studied, or while one is already waiting.
        if (_lab.Done.Contains(id) || _systems.Hold.Count(id) > 0 || _diver.Bag.Contains(id)) return;
        if (c is not Gloam && GD.Randf() > 0.6f) return;
        if (_diver.Bag.Count >= Solitude.Player.Diver.BagSize) { _hud.Message("Something came back on the barbs, but the bag is full. It drifts away.", 4f); return; }
        _diver.Bag.Add(id);
        _hud.Message($"Something came back on the barbs: {Items.Name(id).ToLowerInvariant()}. Bagged for the lab.", 5f);
    }

    // ------------------------------------------------------------------ lure flares

    /// <summary>Helm [B]: release a lure flare astern.</summary>
    private void ReleaseLure()
    {
        if (_systems.Hold.Count(ItemId.LureFlare) == 0)
        {
            _hud.Message(_lab.Has(Discovery.LureFlares) ? "No lure flares in the hold. Prepare them at the lab bench." : "Nothing to release.", 3f);
            return;
        }
        _systems.Hold.Remove(ItemId.LureFlare);
        _lure?.QueueFree();
        _lure = new Node3D { Name = "LureFlare" };
        _game.AddChild(_lure);
        _lure.GlobalPosition = _sub.ToGlobal(new Vector3(0, -2.5f, 15f));
        var glow = Art.World(Art.FlatTexture(new Color(0.3f, 1f, 0.85f)), Colors.White).Glow(new Color(0.3f, 1f, 0.85f), 4f);
        _lure.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.25f, Height = 0.5f, RadialSegments = 6, Rings = 3 }, MaterialOverride = glow });
        _lureLight = new OmniLight3D { LightColor = new Color(0.35f, 1f, 0.85f), LightEnergy = 3f, OmniRange = 22f };
        _lure.AddChild(_lureLight);
        _lureLife = LureLife;
        _audio.PlayAt("airlock", 0f, 0.4f);
        Log("Lure flare released astern.");
    }

    private void UpdateLure(float dt)
    {
        if (_lure == null) return;
        _lureLife -= dt;
        Vector3 p = _lure.GlobalPosition;
        p += (WaterAt(p) + Vector3.Down * 0.08f) * dt;
        float floor = _gen.Height(p.X, p.Z) + 0.3f;
        if (p.Y < floor) p.Y = floor;
        _lure.GlobalPosition = p;
        float fade = Mathf.Clamp(_lureLife / 15f, 0f, 1f);
        _lureLight.LightEnergy = 3f * fade * (0.85f + 0.15f * Mathf.Sin(_lureLife * 7f));
        if (_lureLife <= 0f) { _lure.QueueFree(); _lure = null; }
    }

    /// <summary>A live lure the Gloam can see (it prefers the flare to the boat while it lasts).</summary>
    private bool LureVisible => _lure != null && _lureLife > 5f && _gloam != null && _gloam.Head.DistanceTo(_lure.GlobalPosition) < 300f;

    /// <summary>The Gloam hit the flare instead of you.</summary>
    private void LureEaten()
    {
        _lure?.QueueFree();
        _lure = null;
        _hud.Message("Out in the dark, the flare goes out all at once.", 5f);
        Log("The lure flare went out all at once.");
    }
}
