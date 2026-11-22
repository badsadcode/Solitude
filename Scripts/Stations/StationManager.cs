using System;
using System.Collections.Generic;
using Godot;
using Solitude.Audio;
using Solitude.Sub;
using Solitude.World;

namespace Solitude.Stations;

/// <summary>
/// Everything about stations from the boat's point of view:
///   - streams exteriors near the submarine,
///   - distress beacons (audible within ~1 km; hearing one marks the station Known),
///   - the docking computer: alignment with the berth, lane light colour, capture zone,
///     magnetic clamps that pull the boat into the berth, and undocking,
///   - per-station state that survives leaving and coming back.
/// </summary>
public partial class StationManager : Node3D
{
    public StationDirectory Directory;
    public Submarine Sub;
    public AudioDirector Audio;
    public Action<string> Message;

    public const float StreamRadius = 900f, BeaconRange = 1000f, GuidanceRange = 160f;

    public readonly Dictionary<long, StationState> States = new();
    private readonly Dictionary<long, StationExterior> _exteriors = new();

    public StationSite Nearest { get; private set; }
    public float NearestDistance { get; private set; } = float.MaxValue;
    public StationSite NearestKnown { get; private set; }
    public float NearestKnownDistance { get; private set; } = float.MaxValue;
    public StationSite DockedAt { get; private set; }
    public bool Docking { get; private set; }

    // Docking computer output (relative to the berth, in berth space)
    public float DockDistance, DockLateral, DockVertical, DockHeadingError, DockClosing;
    public bool InCaptureZone;

    private float _beaconTimer = 2f;
    private Vector3 _prevSubPos;

    public StationState StateOf(StationSite s)
    {
        if (!States.TryGetValue(s.Id, out var st)) States[s.Id] = st = new StationState();
        return st;
    }

    public IEnumerable<StationSite> Known(Vector3 around, float radius)
    {
        foreach (var s in Directory.Near(around, radius)) if (s.Known) yield return s;
    }

    public override void _Process(double delta)
    {
        if (Sub == null || Directory == null) return;
        float dt = (float)delta;
        Vector3 sp = Sub.GlobalPosition;

        // --- stream exteriors and find the nearest stations
        var seen = new HashSet<long>();
        Nearest = null; NearestDistance = float.MaxValue;
        NearestKnown = null; NearestKnownDistance = float.MaxValue;
        foreach (var site in Directory.Near(sp, 3000f))
        {
            float d = site.Position.DistanceTo(sp);
            if (d < BeaconRange && !site.Known)
            {
                site.Known = true;
                Message?.Invoke($"BEACON ACQUIRED: {site.Name}. Bearing added to the helm.");
            }
            if (d < NearestDistance) { Nearest = site; NearestDistance = d; }
            if (site.Known && d < NearestKnownDistance) { NearestKnown = site; NearestKnownDistance = d; }
            if (d > StreamRadius) continue;
            seen.Add(site.Id);
            if (!_exteriors.ContainsKey(site.Id))
            {
                var ext = new StationExterior { Site = site, Powered = IsPowered(site), Name = $"Station_{site.Id}" };
                AddChild(ext);
                ext.GlobalTransform = new Transform3D(new Basis(Vector3.Up, site.Yaw), site.Position);
                _exteriors[site.Id] = ext;
            }
        }
        var drop = new List<long>();
        foreach (var id in _exteriors.Keys) if (!seen.Contains(id) && (DockedAt == null || DockedAt.Id != id)) drop.Add(id);
        foreach (var id in drop) { _exteriors[id].QueueFree(); _exteriors.Remove(id); }

        // --- beacon: a faint double pip from the nearest station, panned toward it
        _beaconTimer -= dt;
        if (_beaconTimer <= 0f && Nearest != null && NearestDistance < BeaconRange && DockedAt == null)
        {
            _beaconTimer = 6f;
            Vector3 rel = Sub.GlobalBasis.Inverse() * (Nearest.Position - sp);
            float pan = Mathf.Clamp(rel.X / Mathf.Max(1f, new Vector2(rel.X, rel.Z).Length()), -1f, 1f);
            float gain = Mathf.Clamp(1f - NearestDistance / BeaconRange, 0.08f, 1f);
            Audio?.PlayAt("beacon", pan, gain);
        }

        UpdateDockingComputer(dt);
        _prevSubPos = sp;
    }

    private static bool IsPowered(StationSite s) => new Random(s.Seed).NextDouble() < 0.5;

    // ------------------------------------------------------------------ docking

    private void UpdateDockingComputer(float dt)
    {
        InCaptureZone = false;
        if (Nearest == null || NearestDistance > GuidanceRange + 60f || DockedAt != null) return;
        Transform3D berth = Nearest.DockPose;
        Vector3 rel = berth.Basis.Inverse() * (Sub.GlobalPosition - berth.Origin);
        DockDistance = rel.Length();
        DockLateral = rel.X;
        DockVertical = rel.Y;
        Vector3 subF = -Sub.GlobalBasis.Z; subF.Y = 0;
        Vector3 berthF = -berth.Basis.Z; berthF.Y = 0;
        DockHeadingError = Mathf.RadToDeg(subF.SignedAngleTo(berthF, Vector3.Up));
        float closing = -(Sub.GlobalPosition - _prevSubPos).Dot((Sub.GlobalPosition - berth.Origin).Normalized()) / Mathf.Max(dt, 1e-4f);
        DockClosing = Mathf.Lerp(DockClosing, closing, Mathf.Clamp(5f * dt, 0f, 1f));
        float speed = Sub.LinearVelocity.Length();
        InCaptureZone = DockDistance < 9f && Mathf.Abs(DockHeadingError) < 25f && speed < 1.3f;

        if (_exteriors.TryGetValue(Nearest.Id, out var ext))
        {
            bool aligned = Mathf.Abs(DockLateral) < 3f && Mathf.Abs(DockVertical) < 3f && Mathf.Abs(DockHeadingError) < 15f;
            Color c = speed > 2.5f && DockDistance < 60f ? new Color(1f, 0.15f, 0.1f) : aligned || InCaptureZone ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.65f, 0.1f);
            ext.SetLaneState(c);
        }
    }

    /// <summary>Engage the clamps (if in the capture zone). The boat is pulled into the berth.</summary>
    public bool TryDock(Action onDocked)
    {
        if (!InCaptureZone || Docking || DockedAt != null) return false;
        var site = Nearest;
        Docking = true;
        Sub.LinearVelocity = Vector3.Zero;
        Sub.AngularVelocity = Vector3.Zero;
        Sub.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        Sub.Freeze = true;
        Sub.TelegraphIndex = 2; Sub.ApplyTelegraph();
        Audio?.PlayImpact(0.6f);
        Message?.Invoke("CLAMPS ENGAGED. Hold still.");
        var tw = CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(Sub, "global_transform", site.DockPose, 4.0);
        tw.TweenCallback(Callable.From(() => Audio?.PlayImpact(1.0f)));
        tw.TweenInterval(0.6);
        tw.TweenCallback(Callable.From(() =>
        {
            Audio?.PlayImpact(0.5f);
            Audio?.PlayAt("airlock", 0f, 0.9f);
            Docking = false;
            DockedAt = site;
            Message?.Invoke($"DOCKED: {site.Name}. Airlock equalised — the dive hatch in engineering opens onto the station.");
            onDocked?.Invoke();
        }));
        return true;
    }

    public void Undock()
    {
        if (DockedAt == null) return;
        var site = DockedAt;
        DockedAt = null;
        Sub.Freeze = false;
        Sub.LinearVelocity = Sub.GlobalBasis.Z * 0.8f; // drift back out of the berth
        Audio?.PlayImpact(0.7f);
        Audio?.PlayAt("airlock", 0f, 0.6f);
        Message?.Invoke($"CLAMPS RELEASED. Clear of {site.Name}.");
    }
}
