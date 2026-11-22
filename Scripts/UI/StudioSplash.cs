using System;
using Godot;

namespace Solitude.UI;

/// <summary>
/// The Bad Ideas Works splash (Tom's logo), shown once per launch before the title: the logo deep
/// under an alien sea, resolved by a sonar ping (see Shaders/studio_splash.gdshader). About seven
/// seconds; any key or click skips it. Raises Finished when it's gone.
/// </summary>
public partial class StudioSplash : CanvasLayer
{
    public event Action Finished;
    /// <summary>The ping goes out (the director plays the sound).</summary>
    public event Action Pinged;

    public const float Length = 7.0f, PingAt = 1.4f;
    private ColorRect _rect;
    private ShaderMaterial _mat;
    private float _t, _end = Length;
    private bool _pinged, _done;

    public override void _Ready()
    {
        Layer = 120;
        var tex = GD.Load<Texture2D>("res://assets/branding/biw_logo.png");
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/studio_splash.gdshader") };
        _mat.SetShaderParameter("logo", tex);
        if (tex != null) _mat.SetShaderParameter("img_aspect", tex.GetWidth() / (float)tex.GetHeight());
        _mat.SetShaderParameter("ping_at", PingAt);
        _rect = new ColorRect { Material = _mat, Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
        Process(0f);
    }

    /// <summary>For captures: jump to a moment.</summary>
    public void Seek(float t) { _t = t; Process(0f); }

    public override void _Input(InputEvent e)
    {
        if (_done) return;
        bool skip = e is InputEventKey { Pressed: true, Echo: false } || e is InputEventMouseButton { Pressed: true };
        if (skip && _t > 0.3f && _end > _t + 0.8f) { _end = _t + 0.8f; GetViewport().SetInputAsHandled(); }
    }

    public override void _Process(double delta) => Process((float)delta);

    private void Process(float dt)
    {
        if (_done) return;
        _t += dt;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        _mat.SetShaderParameter("aspect", size.X / Mathf.Max(1f, size.Y));
        _mat.SetShaderParameter("t", _t);
        // No fade-in: the boot splash (assets/branding/boot_splash.png) IS this first frame, so the
        // loading screen flows straight into the animation.
        float fade = Mathf.Clamp((_end - _t) / 0.8f, 0f, 1f);
        _mat.SetShaderParameter("fade", fade);
        if (!_pinged && _t >= PingAt) { _pinged = true; Pinged?.Invoke(); }
        if (_t >= _end)
        {
            _done = true;
            Finished?.Invoke();
            QueueFree();
        }
    }
}
