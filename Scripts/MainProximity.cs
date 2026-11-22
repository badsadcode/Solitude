using System;
using Godot;
using Solitude.Core;
using Solitude.Sub;

namespace Solitude;

/// <summary>
/// Two ideas from BALE IX, built to work together:
///
///  * THE PROXIMITY PANEL: six lamps on the helm console (repeated on the monitor desk and the helm HUD),
///    one per hull camera direction. Amber = something solid close that way; red blinking = something
///    moving that way. It never says what.
///  * PAN / TILT / ZOOM on the hull cameras, from the monitor desk: WASD/arrows aim the selected
///    camera, the wheel zooms, X re-centres. The heads are slow servos, so turning to look takes a while.
///
/// The panel tells you which number to press. The camera shows you what's there, or doesn't.
/// </summary>
public partial class Main
{
    private ProximityPanel _prox;
    private float _camServo;

    private void UpdateProximity(float dt)
    {
        if (_prox == null)
        {
            _prox = new ProximityPanel { Name = "ProximityPanel", Sub = _sub, Bodies = () => _sonar.Bodies?.Invoke() };
            _game.AddChild(_prox);
            _prox.Tick += OnProxTick;
        }
        _prox.Active = _mode != Mode.Title && !_endingRunning;

        float glow = 0f; Color glowCol = ProximityPanel.Amber;
        for (int i = 0; i < ProximityPanel.Count; i++)
        {
            float e = _prox.LampEnergy[i];
            Color c = _prox.LampColor[i];
            for (int k = i; k < _interior.ProxLamps.Count; k += ProximityPanel.Count)
            {
                _interior.ProxLamps[k].SetShaderParameter("emission_color", c);
                _interior.ProxLamps[k].SetShaderParameter("emission_energy", e);
            }
            _hud.Prox.Col[i] = c; _hud.Prox.Energy[i] = e;
            _hud.FeedProx.Col[i] = c; _hud.FeedProx.Energy[i] = e;
            if (e > glow) { glow = e; glowCol = c; }
        }
        _interior.ProxGlow.LightColor = glowCol;
        _interior.ProxGlow.LightEnergy = glow * 0.08f;
        _hud.FeedProx.Selected = _mode == Mode.Monitors ? _selectedFeed : -1;
    }

    private void OnProxTick(int lamp, bool first)
    {
        // Only heard aboard (the relay is on the helm console).
        if (_diver.Active || _player.InStation || _mode == Mode.Title) return;
        if (first) _audio.PlayBlip(2100f, 0.012f, 0.35f);   // a relay clicks over
        else _audio.PlayBlip(1600f, 0.01f, 0.14f);          // ...and keeps ticking when it's close
    }

    /// <summary>Monitor desk: aim and zoom the selected hull camera. Called from UpdateMonitors.</summary>
    private void UpdateCameraAim(float dt)
    {
        float yaw = Input.GetAxis(GameInput.Left, GameInput.Right);
        float pitch = Input.GetAxis(GameInput.Back, GameInput.Forward);
        bool moved = _rig.Aim(_selectedFeed, yaw, pitch, dt);
        if (Input.IsActionJustPressed(GameInput.CenterControls))
        {
            _rig.Centre(_selectedFeed);
            _audio.PlayBlip(500f, 0.05f, 0.4f);
        }
        _camServo = Mathf.MoveToward(_camServo, moved || _rig.Zooming(_selectedFeed) ? 1f : 0f, dt * 6f);

        var f = _rig.Feeds[_selectedFeed];
        string aim = _rig.AimText(_selectedFeed);
        _interior.BigScreenLabel.Text = f.ZoomShown > 1.05f ? $"{f.Name}  {f.ZoomShown:0.0}x" : f.Name;
        if (_feedFullscreen)
            _hud.ShowFeed(f.Viewport.GetTexture(), f.Name, f.Signal, $"[1-6] switch   [F] close\n{aim}    [WASD] aim   [WHEEL] zoom   [X] centre");
    }

    private void CameraZoomInput(InputEventMouseButton wheel)
    {
        _rig.ZoomStep(_selectedFeed, wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1);
    }
}
