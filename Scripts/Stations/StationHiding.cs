using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Creatures;
using Solitude.Interior;

namespace Solitude.Stations;

/// <summary>
/// Hiding (NoorLevel `hiding_spot` markers, in front of lockers and at desks).
///
/// E: climb into the locker / crawl under the desk. The view narrows to the slats (or the gap under
/// the desk top), your breathing gets loud, and you can only look a little. Crawlers can't see you.
/// One that comes close stops and sniffs. Then it either moves on, or it found you.
///   SPACE (hold): hold your breath. Much harder to find, but only for about nine seconds; run out
///                 and you gasp, and everything nearby hears it.
///   A torch left on inside a locker is a lantern in a box. Turn it off.
///   E again: get out.
/// </summary>
public partial class StationInterior
{
    private readonly List<(Vector3 stand, Vector3 eyeFloor, float yaw, bool desk)> _hideSpots = new();
    private float _breath = 1f;          // 0..1 of a held breath (≈9 s)
    /// <summary>Which kind of hiding place you're in (for the view overlay).</summary>
    public bool HidingUnderDesk { get; private set; }
    private bool _holding, _gasped;
    private const float BreathSeconds = 9f;

    public bool HoldingBreath => _holding;
    public float Breath01 => _breath;

    /// <summary>Called from DressStation for each hiding_spot marker.</summary>
    private void AddHidingSpot(Vector3 pos, Godot.Collections.Dictionary extras)
    {
        bool desk = Field(extras, "class").AsString() == "desk";
        // The thing you hide in: the prop the marker points at (its forward is where you look out).
        string on = Field(extras, "on").AsString();
        Vector3 propPos = pos;
        Basis propBasis = Basis.Identity;
        for (int i = 0; i < _markers.Count; i++)
            if (_markers[i].kind == "prop" && Field(_markers[i].extras, "id").AsString() == on) { propPos = _markers[i].pos; propBasis = _markerBasis[i]; break; }
        Vector3 fwd = propBasis.Z; fwd.Y = 0; fwd = fwd.LengthSquared() > 0.01f ? fwd.Normalized() : Vector3.Back;
        // Locker: look out of its front (its +Z, where the door is). Desk: its front is the knee side,
        // so under it you face the other way, out of the open back into the room.
        float yaw = Mathf.Atan2(-fwd.X, -fwd.Z) + (desk ? Mathf.Pi : 0f);
        // Locker: the eye sits at the door's vents (just inside the front face, which is 0.25 m out from
        // the locker's centre), so the slits look out at the room, not at the inside of the door.
        _hideSpots.Add((pos, propPos + fwd * (desk ? 0.05f : 0.3f), yaw, desk));
        int index = _hideSpots.Count - 1;
        Interactable.Create(this, pos + Vector3.Up * 0.9f, new Vector3(1.0f, 1.6f, 1.0f),
            desk ? "Crawl under the desk (hide)" : "Climb into the locker (hide)", () => EnterHiding(index));
    }

    private void EnterHiding(int index)
    {
        if (Player == null || Player.Hidden) return;
        var (stand, eyeFloor, yaw, desk) = _hideSpots[index];
        HidingUnderDesk = desk;
        Player.Hide(ToGlobal(eyeFloor), GlobalRotation.Y + yaw, desk, new Transform3D(GlobalBasis, ToGlobal(stand)));
        _breath = 1f; _holding = false; _gasped = false;
        Sound?.Invoke(desk ? "empty" : "door", Player.GlobalPosition);
        if (!_toldHiding)
        {
            _toldHiding = true;
            Message?.Invoke(desk ? "Under the desk. Knees to your chest. Hold SPACE to hold your breath. E to get out." :
                "Inside the locker. It smells of someone. Hold SPACE to hold your breath. Turn the torch off. E to get out.");
        }
    }
    private static bool _toldHiding;

    /// <summary>Dev / capture: hide in the n-th hiding place.</summary>
    public bool DebugHide(int n = 0) { if (n >= _hideSpots.Count) return false; EnterHiding(n); return true; }
    public int HidingSpotCount => _hideSpots.Count;

    public void LeaveHiding()
    {
        if (Player == null || !Player.Hidden) return;
        Player.Unhide();
        _holding = false;
        Sound?.Invoke("door", Player.GlobalPosition);
    }

    /// <summary>A crawler sniffing at your hiding place decides. True = found you.</summary>
    private bool Sniffed(Crawler c)
    {
        double chance = _holding ? 0.07 : 0.35;
        if (Player.Flashlight.Visible) chance = 0.85;   // a lantern in a box
        bool found = _rng.NextDouble() < chance;
        GD.Print($"[hide] sniffed: chance {chance:0.00} holding={_holding} torch={Player.Flashlight.Visible} -> {(found ? "FOUND" : "moved on")}");
        if (!found) return false;
        Player.Unhide();      // ...and its lunge does the rest
        _holding = false;
        Sound?.Invoke("screech", c.GlobalPosition);
        Message?.Invoke(Player.Flashlight.Visible ? "The light through the slats. It saw the light." : "The door is torn open.");
        return true;
    }

    private void UpdateHiding(float dt)
    {
        if (Player == null || !Player.Hidden) { _breath = Mathf.Min(1f, _breath + dt / 4f); _holding = false; return; }
        bool want = Input.IsActionPressed(GameInput.Jump) && !_gasped;
        if (want && _breath > 0f)
        {
            _holding = true;
            _breath = Mathf.Max(0f, _breath - dt / BreathSeconds);
            if (_breath <= 0f)
            {
                // Out of air: a loud, ragged gasp. Everything close hears it.
                _holding = false;
                _gasped = true;
                Sound?.Invoke("hurt", Player.GlobalPosition);
                foreach (var c in Crawlers) c.Hear(Player.GlobalPosition, 14f);
                Message?.Invoke("You gasp. Too loud.");
            }
        }
        else
        {
            _holding = false;
            _breath = Mathf.Min(1f, _breath + dt / 6f);
            if (!Input.IsActionPressed(GameInput.Jump)) _gasped = false;
        }
    }

    /// <summary>For the heartbeat: how close the nearest live crawler is (m).</summary>
    public float NearestCrawler()
    {
        float best = float.MaxValue;
        if (Player == null) return best;
        foreach (var c in Crawlers) if (c.Current != Crawler.State.Dead) best = Mathf.Min(best, c.GlobalPosition.DistanceTo(Player.GlobalPosition));
        return best;
    }
}
