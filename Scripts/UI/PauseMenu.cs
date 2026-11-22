using System;
using Godot;
using Solitude.Systems;

namespace Solitude.UI;

/// <summary>
/// In-dive pause menu, styled like the title: Resume · Settings · Save &amp; return to title · Quit.
/// Runs while the tree is paused (ProcessMode.Always). Settings changes apply live.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    public Action OnResume, OnSaveAndTitle, OnQuit, OnSettingsChanged;

    private Control _root, _main, _settings;
    private static readonly Color Phosphor = new(0.55f, 0.75f, 0.62f), Bright = new(0.85f, 1f, 0.88f);

    public override void _Ready()
    {
        Layer = 20;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;

        var font = new SystemFont { FontNames = new[] { "Consolas", "Lucida Console", "Courier New", "monospace" }, Antialiasing = TextServer.FontAntialiasing.None };
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop, Theme = new Theme { DefaultFont = font, DefaultFontSize = 15 } };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var dim = new ColorRect { Color = new Color(0, 0.01f, 0.01f, 0.72f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        var title = Label("S O L I T U D E", 40, new Color(0.8f, 0.85f, 0.8f));
        title.Position = new Vector2(56, 44);
        _root.AddChild(title);
        var paused = Label("dive paused. the dark waits.", 15, new Color(0.5f, 0.6f, 0.56f));
        paused.Position = new Vector2(60, 98);
        _root.AddChild(paused);

        _main = Column();
        Button("RESUME", _main, () => OnResume?.Invoke());
        Button("SETTINGS", _main, () => ShowSettings(true));
        Button("SAVE & RETURN TO TITLE", _main, () => OnSaveAndTitle?.Invoke());
        Button("QUIT TO DESKTOP", _main, () => OnQuit?.Invoke());

        _settings = Column();
        _settings.Visible = false;
        Slider("MASTER VOLUME", 0f, 1f, Settings.MasterVolume, v => { Settings.MasterVolume = v; Changed(); });
        Slider("MUSIC VOLUME", 0f, 1f, Settings.MusicVolume, v => { Settings.MusicVolume = v; Changed(); });
        Slider("MOUSE SENSITIVITY", 0.3f, 2f, Settings.MouseSensitivity, v => { Settings.MouseSensitivity = v; Changed(); });
        var fs = new CheckButton { Text = "  FULLSCREEN", ButtonPressed = Settings.Fullscreen, FocusMode = Control.FocusModeEnum.All };
        fs.AddThemeColorOverride("font_color", Phosphor);
        fs.AddThemeColorOverride("font_hover_color", Bright);
        fs.AddThemeFontSizeOverride("font_size", 18);
        fs.Toggled += on => { Settings.Fullscreen = on; Changed(); };
        _settings.AddChild(fs);
        Button("BACK", _settings, () => ShowSettings(false));
    }

    private void Changed()
    {
        Settings.Apply();
        Settings.Save();
        OnSettingsChanged?.Invoke();
    }

    private VBoxContainer Column()
    {
        var v = new VBoxContainer { Position = new Vector2(56, 160) };
        v.AddThemeConstantOverride("separation", 6);
        _root.AddChild(v);
        return v;
    }

    private static Label Label(string text, int size, Color c)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    private Button Button(string text, Control parent, Action act)
    {
        var b = new Button { Text = "  " + text, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 20);
        b.AddThemeColorOverride("font_color", Phosphor);
        b.AddThemeColorOverride("font_hover_color", Bright);
        b.AddThemeColorOverride("font_focus_color", Bright);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.FocusEntered += () => b.Text = "> " + text;
        b.FocusExited += () => b.Text = "  " + text;
        b.MouseEntered += () => b.GrabFocus();
        b.Pressed += () => act();
        parent.AddChild(b);
        return b;
    }

    private void Slider(string text, float min, float max, float value, Action<float> onChange)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var l = Label("  " + text, 16, Phosphor);
        l.CustomMinimumSize = new Vector2(240, 0);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = 0.05, Value = value, CustomMinimumSize = new Vector2(220, 24), FocusMode = Control.FocusModeEnum.All };
        var pct = Label($"{Mathf.RoundToInt(value * 100)}%", 16, Bright);
        s.ValueChanged += v => { pct.Text = $"{Mathf.RoundToInt((float)v * 100)}%"; onChange((float)v); };
        row.AddChild(l); row.AddChild(s); row.AddChild(pct);
        _settings.AddChild(row);
    }

    public void ShowSettings(bool on)
    {
        _main.Visible = !on;
        _settings.Visible = on;
        (on ? _settings.GetChild<Control>(0) : _main.GetChild<Control>(1)).CallDeferred(Control.MethodName.GrabFocus);
    }

    public void Open()
    {
        Visible = true;
        ShowSettings(false);
        _main.GetChild<Control>(0).CallDeferred(Control.MethodName.GrabFocus);
    }

    public void Close() => Visible = false;

    // Esc again resumes (or backs out of Settings). This node keeps running while the tree is paused.
    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape }) return;
        GetViewport().SetInputAsHandled();
        if (_settings.Visible) ShowSettings(false);
        else OnResume?.Invoke();
    }
}
