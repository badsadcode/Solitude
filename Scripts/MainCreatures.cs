using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Creatures;

namespace Solitude;

/// <summary>Tom's creatures beyond the ravager: Crystal Rays and the Fangwyrm (CreatureDirector).</summary>
public partial class Main
{
    private CreatureDirector _creatures;

    private void SetupCreatures()
    {
        _creatures = new CreatureDirector
        {
            Name = "Creatures", Gen = _gen, Audio = _audio,
            Focus = FocusPoint,
            FocusIsDiver = () => _diver.Active,
            DiverTimeOutside = () => _diver.TimeOutside,
            LightsOn = () => _diver.Active ? _diver.Lamp.Visible : _sub.LightsOn,
        };
        _game.AddChild(_creatures);

        _creatures.HullBrush += strength =>
        {
            if (_diver.Active) return;
            _sub.Damage(8f);
            _audio.PlayImpact(strength);
            _audio.PlayGroan(0.9f);
            _player.AddShake(strength * 0.8f);
            _damageFlash = Mathf.Min(1f, _damageFlash + 0.4f);
            _hud.Message("Something enormous dragged itself along the hull.", 6f);
        };
        _creatures.BiteDiver += dmg =>
        {
            if (!_diver.Active) return;
            _player.Damage(dmg);
            _audio.PlayImpact(2f);
            _hud.Message("Teeth. Everywhere.", 4f);
        };

        // Sonar hears them too (on top of whatever the stations milestone registered).
        var previous = _sonar.Bodies;
        _sonar.Bodies = () =>
        {
            var list = new List<Vector3>(previous?.Invoke() ?? new List<Vector3>());
            list.AddRange(_creatures.Bodies());
            return list;
        };
    }

    private void UpdateCreatureDebugKeys()
    {
        if (Input.IsActionJustPressed(GameInput.SummonRays)) { _creatures.SummonRays(); _hud.Message("[debug] crystal rays nearby", 3f); }
        if (Input.IsActionJustPressed(GameInput.SummonWyrm)) { _creatures.SummonWyrm(); _hud.Message("[debug] the Fangwyrm is passing", 3f); }
        if (Input.IsActionJustPressed(GameInput.SummonWhale))
        {
            // Across the bow, ~45 m out, entering CAM 1's view within seconds.
            Vector3 fwd = -_sub.GlobalBasis.Z; fwd.Y = 0; fwd = fwd.Normalized();
            Vector3 side = new(-fwd.Z, 0, fwd.X);
            Vector3 centre = _sub.GlobalPosition + fwd * 45f + Vector3.Up * 4f;
            _creatures.SummonWhaleBetween(centre - side * 30f, centre + side * 420f);
            _hud.Message("[debug] something very large is crossing the bow (CAM 1)", 4f);
        }
    }
}
