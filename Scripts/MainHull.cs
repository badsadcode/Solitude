using Godot;
using Solitude.Core;
using Solitude.Interior;

namespace Solitude;

/// <summary>Glue for HullEvents: breaches, flooding, steam, drips, knocks.</summary>
public partial class Main
{
    private HullEvents _hull;

    private void SetupHullEvents()
    {
        _hull = new HullEvents
        {
            Name = "HullEvents", Interior = _interior, Sub = _sub, Systems = _systems, Audio = _audio, Player = _player,
            Message = m => _hud.Message(m, 5f),
            Sound = PlaySpatial,
            Aboard = () => _mode != Mode.Title && !_player.InStation && !_diver.Active,
            Docked = () => _stations.DockedAt != null || _stations.Docking,
        };
        _interior.AddChild(_hull);
        _sub.Impact += s => { if (_mode != Mode.Title) _hull.OnImpact(s); };
    }

    private void UpdateHullDebugKeys()
    {
        if (Input.IsActionJustPressed(GameInput.DebugBreach)) _hull.OpenBreach();
        if (Input.IsActionJustPressed(GameInput.DebugKnock)) _hull.Knock();
    }
}
