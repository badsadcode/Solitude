using System.Text;
using Godot;
using Solitude.Sub;

namespace Solitude.UI;

/// <summary>
/// Full-resolution overlay drawn on top of the low-res game view. Deliberately sparse:
/// prompts while walking, an instrument block at the helm, help text at stations.
/// </summary>
public partial class Hud : CanvasLayer
{
    private static readonly Color Phosphor = new(0.55f, 1f, 0.7f);
    private static readonly Color Amber = new(1f, 0.7f, 0.25f);
    private static readonly Color Warn = new(1f, 0.25f, 0.2f);

    private Control _root;
    private Label _prompt, _message, _helmLeft, _helmRight, _help, _warning, _title, _subtitle, _feedName;
    private ColorRect _crosshair, _fade;
    private TextureRect _feedOverlay;
    private YokeGauge _yoke;
    private Control _helmGroup;
    private float _messageTime, _warnBlink;

    public override void _Ready()
    {
        Layer = 10;
        var font = new SystemFont { FontNames = new[] { "Consolas", "Lucida Console", "Courier New", "monospace" }, Antialiasing = TextServer.FontAntialiasing.None };
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = new Theme { DefaultFont = font, DefaultFontSize = 15 } };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _feedOverlay = new TextureRect
        {
            Visible = false,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/crt_canvas.gdshader") },
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _feedOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var feedBg = new ColorRect { Color = Colors.Black, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Name = "FeedBg" };
        feedBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(feedBg);
        _root.AddChild(_feedOverlay);
        _feedName = MakeLabel(Phosphor, 20);
        _feedName.Position = new Vector2(32, 58);   // below the vitals line (y 34), which stays visible
        _feedName.Visible = false;
        _root.AddChild(_feedName);

        _crosshair = new ColorRect { Color = new Color(1, 1, 1, 0.6f), Size = new Vector2(3, 3), MouseFilter = Control.MouseFilterEnum.Ignore };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.Center);
        _crosshair.Position -= new Vector2(1.5f, 1.5f);
        _root.AddChild(_crosshair);

        _prompt = MakeLabel(new Color(0.9f, 0.9f, 0.85f), 16);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        _prompt.GrowHorizontal = Control.GrowDirection.Both;
        _prompt.Position += new Vector2(0, -150);
        _root.AddChild(_prompt);

        _message = MakeLabel(Amber, 15);
        _message.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.GrowHorizontal = Control.GrowDirection.Both;
        _message.Position += new Vector2(0, -110);
        _root.AddChild(_message);

        _helmGroup = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _helmGroup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_helmGroup);
        _helmLeft = MakeLabel(Phosphor, 15);
        _helmLeft.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _helmLeft.GrowVertical = Control.GrowDirection.Begin;
        _helmLeft.Position += new Vector2(24, -24);
        _helmGroup.AddChild(_helmLeft);
        _helmRight = MakeLabel(Phosphor, 15);
        _helmRight.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _helmRight.GrowHorizontal = Control.GrowDirection.Begin;
        _helmRight.GrowVertical = Control.GrowDirection.Begin;
        _helmRight.HorizontalAlignment = HorizontalAlignment.Right;
        _helmRight.Position += new Vector2(-24, -24);
        _helmGroup.AddChild(_helmRight);
        _yoke = new YokeGauge { Size = new Vector2(110, 84), MouseFilter = Control.MouseFilterEnum.Ignore };
        _yoke.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _yoke.Position += new Vector2(-140, 24);
        _helmGroup.AddChild(_yoke);
        Prox = new ProxStrip { Size = new Vector2(176, 30), MouseFilter = Control.MouseFilterEnum.Ignore };
        Prox.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        Prox.Position += new Vector2(-200, 122);
        _helmGroup.AddChild(Prox);
        FeedProx = new ProxStrip { Size = new Vector2(176, 30), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        FeedProx.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        FeedProx.Position += new Vector2(-200, 62);
        _root.AddChild(FeedProx);   // after the feed overlay, so it draws on top of it

        _warning = MakeLabel(Warn, 20);
        _warning.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _warning.HorizontalAlignment = HorizontalAlignment.Center;
        _warning.GrowHorizontal = Control.GrowDirection.Both;
        _warning.Position += new Vector2(0, 30);
        _root.AddChild(_warning);

        _help = MakeLabel(new Color(0.7f, 0.75f, 0.7f, 0.8f), 13);
        _help.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _help.HorizontalAlignment = HorizontalAlignment.Center;
        _help.GrowHorizontal = Control.GrowDirection.Both;
        _help.Position += new Vector2(0, 8);
        _root.AddChild(_help);

        _fade = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_fade);

        _title = MakeLabel(new Color(0.8f, 0.85f, 0.8f), 42);
        _title.SetAnchorsPreset(Control.LayoutPreset.Center);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.GrowHorizontal = Control.GrowDirection.Both;
        _title.GrowVertical = Control.GrowDirection.Both;
        _title.Position += new Vector2(0, -30);
        _root.AddChild(_title);
        _subtitle = MakeLabel(new Color(0.55f, 0.65f, 0.6f), 16);
        _subtitle.SetAnchorsPreset(Control.LayoutPreset.Center);
        _subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        _subtitle.GrowHorizontal = Control.GrowDirection.Both;
        _subtitle.Position += new Vector2(0, 30);
        _root.AddChild(_subtitle);
    }

    private static Label MakeLabel(Color c, int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        l.AddThemeConstantOverride("shadow_offset_x", 2);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        return l;
    }

    // ------------------------------------------------------------ API

    public void Intro(string title, string subtitle, float hold = 2.5f)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _fade.Modulate = Colors.White;
        _title.Modulate = new Color(1, 1, 1, 0);
        _subtitle.Modulate = new Color(1, 1, 1, 0);
        var tw = CreateTween();
        tw.TweenProperty(_title, "modulate:a", 1f, 1.5f);
        tw.Parallel().TweenProperty(_subtitle, "modulate:a", 1f, 2.5f);
        tw.TweenInterval(hold);
        tw.TweenProperty(_title, "modulate:a", 0f, 1.5f);
        tw.Parallel().TweenProperty(_subtitle, "modulate:a", 0f, 1.5f);
        tw.Parallel().TweenProperty(_fade, "modulate:a", 0f, 3f);
    }

    /// <summary>Fade the screen to black (1) or back (0).</summary>
    public void FadeTo(float alpha, float seconds) => CreateTween().TweenProperty(_fade, "modulate:a", alpha, seconds);

    public void SetPrompt(string text) => _prompt.Text = text ?? "";
    public void SetCrosshair(bool on) => _crosshair.Visible = on;
    public void SetHelp(string text) => _help.Text = text ?? "";

    /// <summary>Every message shown (the ship's log picks out the ones worth keeping).</summary>
    public event System.Action<string> Messaged;

    public void Message(string text, float seconds = 5f)
    {
        Messaged?.Invoke(text);
        _message.Text = text;
        _messageTime = seconds;
        _message.Modulate = Colors.White;
    }

    public void ShowHelm(bool on) => _helmGroup.Visible = on;
    public ProxStrip Prox { get; private set; }
    public ProxStrip FeedProx { get; private set; }

    // ---- the ship's log terminal / journal, full screen
    private TextureRect _term;
    private ColorRect _termBg;
    private bool _termCrt;

    public void ShowTerminal(Texture2D tex, bool crt = true)
    {
        if (_term == null)
        {
            _termBg = new ColorRect { Color = new Color(0, 0, 0, 0.92f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _termBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_termBg);
            _term = new TextureRect
            {
                Visible = false, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Linear,
                Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/terminal_canvas.gdshader") },
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _term.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_term);
        }
        bool on = tex != null;
        _term.Visible = on; _termBg.Visible = on;
        if (!on) return;
        _term.Texture = tex;
        _termCrt = crt;
        ((ShaderMaterial)_term.Material).SetShaderParameter("crt", crt);
    }

    /// <summary>Window pixel → terminal pixel (0..640, 0..480), through the same curve the shader draws.</summary>
    public Vector2 TerminalPoint(Vector2 window)
    {
        if (_term == null) return new Vector2(-1, -1);
        Vector2 size = _term.Size;
        float k = Mathf.Min(size.X / 640f, size.Y / 480f);
        Vector2 drawn = new(640f * k, 480f * k), off = (size - drawn) * 0.5f + _term.GlobalPosition;
        Vector2 uv = (window - off) / drawn;
        if (_termCrt) { Vector2 cc = uv - new Vector2(0.5f, 0.5f); uv = new Vector2(0.5f, 0.5f) + cc * (1f + 0.06f * cc.Dot(cc)); }
        return uv * new Vector2(640f, 480f);
    }

    public void ShowFeed(Texture2D tex, string name, float signal, string hint = "[1-6] switch   [F] close")
    {
        bool on = tex != null;
        _feedOverlay.Visible = on;
        // The lamp row rides along on hull-camera feeds (not on the sonar / hydrophone scopes).
        FeedProx.Visible = on && name != null && name.StartsWith("CAM");
        _root.GetNode<ColorRect>("FeedBg").Visible = on;
        _feedName.Visible = on;
        if (!on) return;
        _feedOverlay.Texture = tex;
        _feedName.Text = $"{name}    {hint}";
        ((ShaderMaterial)_feedOverlay.Material).SetShaderParameter("signal", signal);
    }

    public void UpdateHelm(Submarine sub, float altitude, string viewName)
    {
        float kn = sub.ForwardSpeed * 1.944f;
        var sb = new StringBuilder();
        sb.AppendLine($"DEPTH    {sub.Depth,7:0.0} m    ALT {altitude,5:0} m");
        sb.AppendLine($"SPEED    {sub.ForwardSpeed,7:0.00} m/s  ({kn:0.0} kn)");
        sb.AppendLine($"VERT     {sub.VerticalSpeed,7:+0.00;-0.00} m/s");
        sb.AppendLine($"HEADING  {Mathf.RoundToInt(sub.Heading) % 360,7:000}°");
        sb.AppendLine($"PITCH    {sub.PitchDeg,7:+0.0;-0.0}°   ROLL {sub.RollDeg:+0.0;-0.0}°");
        sb.AppendLine($"BALLAST  {sub.Ballast,7:+0.00;-0.00}   {(sub.BallastInput > 0 ? "BLOWING" : sub.BallastInput < 0 ? "FLOODING" : "")}");
        sb.AppendLine($"HULL     {sub.Hull,6:0}%    {(sub.LightsOn ? (sub.RedFloods ? "FLOODS RED" : "FLOODS ON") : "FLOODS OFF")}");
        sb.Append($"VIEW     {viewName}");
        _helmLeft.Text = sb.ToString();

        var rb = new StringBuilder();
        rb.AppendLine(sub.Mode == DriveMode.Telegraph ? "DRIVE: TELEGRAPH  [T]" : "DRIVE: HOLD-TO-RUN  [T]");
        rb.AppendLine();
        if (sub.Mode == DriveMode.Telegraph)
        {
            for (int i = Submarine.Telegraph.Length - 1; i >= 0; i--)
                rb.AppendLine(i == sub.TelegraphIndex ? $"> {Submarine.Telegraph[i].name} <" : $"  {Submarine.Telegraph[i].name}  ");
        }
        else
        {
            rb.AppendLine(sub.TargetThrottle > 0 ? "> AHEAD <" : "  AHEAD  ");
            rb.AppendLine(sub.TargetThrottle == 0 ? "> IDLE <" : "  IDLE  ");
            rb.AppendLine(sub.TargetThrottle < 0 ? "> ASTERN <" : "  ASTERN  ");
        }
        rb.AppendLine();
        rb.Append($"ENGINE OUT {sub.EngineOutput * 100f,4:0}%");
        _helmRight.Text = rb.ToString();

        _yoke.Rudder = sub.Rudder;
        _yoke.Planes = sub.Planes;
        _yoke.Ballast = sub.Ballast;
    }

    public void SetWarning(string text) => _warning.Text = text ?? "";

    // ------------------------------------------------------------ milestone 3: vitals, panels, arm, hold

    private Label _vitals, _hold, _armInfo;
    private PanelContainer _panel;
    private Label _panelTitle, _panelBody;
    private VBoxContainer _panelButtons;
    private ScrollContainer _panelScroll;
    private readonly System.Collections.Generic.List<int> _panelKeys = new();

    /// <summary>Number keys skip section headers: key n → the n-th real option's index.</summary>
    public int PanelKeyToIndex(int n) => n >= 0 && n < _panelKeys.Count ? _panelKeys[n] : -1;
    private Control _armGroup;

    private void EnsureM3()
    {
        if (_vitals != null) return;
        _vitals = MakeLabel(Phosphor, 14);
        _vitals.Position = new Vector2(16, 34);
        _root.AddChild(_vitals);

        _hold = MakeLabel(new Color(0.8f, 0.85f, 0.75f), 14);
        _hold.Position = new Vector2(16, 60);
        _hold.Visible = false;
        _root.AddChild(_hold);

        _panel = new PanelContainer { Visible = false };
        var style = new StyleBoxFlat { BgColor = new Color(0.02f, 0.05f, 0.04f, 0.88f), BorderColor = new Color(0.3f, 0.8f, 0.5f, 0.6f) };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(14);
        _panel.AddThemeStyleboxOverride("panel", style);
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.Both;
        _panel.Position += new Vector2(-40, 0);
        _panel.CustomMinimumSize = new Vector2(560, 0);
        _root.AddChild(_panel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(v);
        _panelTitle = MakeLabel(Amber, 20);
        _panelBody = MakeLabel(new Color(0.75f, 0.85f, 0.78f), 13);
        _panelBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _panelBody.CustomMinimumSize = new Vector2(530, 0);
        _panelButtons = new VBoxContainer();
        // Long lists (modules) scroll instead of running off the screen.
        _panelScroll = new ScrollContainer { CustomMinimumSize = new Vector2(530, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _panelButtons.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _panelScroll.AddChild(_panelButtons);
        v.AddChild(_panelTitle);
        v.AddChild(_panelBody);
        v.AddChild(new HSeparator());
        v.AddChild(_panelScroll);

        _armGroup = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _armGroup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_armGroup);
        var reticle = new Label { Text = "+", MouseFilter = Control.MouseFilterEnum.Ignore };
        reticle.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.25f, 0.7f));
        reticle.AddThemeFontSizeOverride("font_size", 28);
        reticle.SetAnchorsPreset(Control.LayoutPreset.Center);
        reticle.Position -= new Vector2(8, 18);
        _armGroup.AddChild(reticle);
        _armInfo = MakeLabel(Amber, 16);
        _armInfo.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _armInfo.GrowVertical = Control.GrowDirection.Begin;
        _armInfo.Position += new Vector2(24, -24);
        _armGroup.AddChild(_armInfo);
    }

    public void SetVitals(string text) { EnsureM3(); _vitals.Text = text; }

    private HelmetOverlay _helmet;
    public HelmetOverlay Helmet
    {
        get
        {
            if (_helmet == null)
            {
                _helmet = new HelmetOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
                _helmet.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _root.AddChild(_helmet);
                _root.MoveChild(_helmet, _fade.GetIndex()); // under the fade, over the world
            }
            return _helmet;
        }
    }

    public void ShowHelmet(bool on) => Helmet.Visible = on;

    private HideOverlay _hide;
    /// <summary>The view from inside a locker / under a desk.</summary>
    public HideOverlay Hide
    {
        get
        {
            if (_hide == null)
            {
                _hide = new HideOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
                _hide.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _root.AddChild(_hide);
                _root.MoveChild(_hide, _fade.GetIndex());
            }
            return _hide;
        }
    }

    // ------------------------------------------------------------ title screen

    private Control _titleGroup;

    /// <summary>Hide/show everything that belongs to playing (vitals, prompts, help, crosshair).</summary>
    public void SetGameplayHud(bool on)
    {
        EnsureM3();
        _vitals.Visible = on;
        _help.Visible = on;
        _prompt.Visible = on;
        _warning.Visible = on;
        _message.Visible = on;
        _crosshair.Visible = on;
    }

    /// <summary>
    /// Title in the top-left corner (same face as the intro), a quiet menu beneath it, studio and
    /// seed in the bottom-left. The live world plays behind it.
    /// </summary>
    public void ShowTitle(int seed, System.Action onNewDive, System.Action onQuit, System.Action onContinue = null)
    {
        _titleGroup?.QueueFree();
        _titleGroup = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _titleGroup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_titleGroup);
        _root.MoveChild(_titleGroup, _fade.GetIndex()); // under the fade

        var title = MakeLabel(new Color(0.8f, 0.85f, 0.8f), 46);
        title.Text = "S O L I T U D E";
        title.Position = new Vector2(56, 44);
        _titleGroup.AddChild(title);
        var sub = MakeLabel(new Color(0.5f, 0.6f, 0.56f), 15);
        sub.Text = "five hundred metres down, nobody answers";
        sub.Position = new Vector2(60, 104);
        _titleGroup.AddChild(sub);

        var menu = new VBoxContainer { Position = new Vector2(56, 168) };
        menu.AddThemeConstantOverride("separation", 4);
        _titleGroup.AddChild(menu);
        Button first = null;
        var items = new System.Collections.Generic.List<(string, System.Action)>();
        if (onContinue != null) items.Add(("CONTINUE", onContinue));
        items.Add(("NEW DIVE", onNewDive));
        items.Add(("QUIT", onQuit));
        foreach (var (label, action) in items)
        {
            var b = new Button { Text = "  " + label, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.All };
            b.AddThemeFontSizeOverride("font_size", 20);
            b.AddThemeColorOverride("font_color", new Color(0.55f, 0.75f, 0.62f));
            b.AddThemeColorOverride("font_hover_color", new Color(0.85f, 1f, 0.88f));
            b.AddThemeColorOverride("font_focus_color", new Color(0.85f, 1f, 0.88f));
            b.AddThemeColorOverride("font_pressed_color", Amber);
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            string plain = "  " + label, marked = "> " + label;
            b.FocusEntered += () => b.Text = marked;
            b.FocusExited += () => b.Text = plain;
            b.MouseEntered += () => b.GrabFocus();
            b.Pressed += () => action?.Invoke();
            menu.AddChild(b);
            first ??= b;
        }

        var foot = MakeLabel(new Color(0.4f, 0.48f, 0.45f), 12);
        foot.Text = $"BAD IDEAS WORKS   ·   v{ProjectSettings.GetSetting("application/config/version", "dev")}   ·   seed {seed}";
        foot.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        foot.GrowVertical = Control.GrowDirection.Begin;
        foot.Position += new Vector2(60, -28);
        _titleGroup.AddChild(foot);

        // Bottom right, quietly: the engine it's built on (Tom: "subtle, on the title screen").
        // If assets/branding/godot_logo.png exists it's shown small and pixelated beside the words.
        var engine = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        engine.AddThemeConstantOverride("separation", 8);
        engine.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        engine.GrowHorizontal = Control.GrowDirection.Begin;
        engine.GrowVertical = Control.GrowDirection.Begin;
        engine.Position += new Vector2(-60, -34);
        engine.Modulate = new Color(1, 1, 1, 0.55f);
        // With the logo (Tom's file, pre-pixelated to 30 px tall): "MADE WITH" + the Godot wordmark.
        var logo = ResourceLoader.Exists("res://assets/branding/godot_logo.png") ? GD.Load<Texture2D>("res://assets/branding/godot_logo.png") : null;
        var made = MakeLabel(new Color(0.4f, 0.48f, 0.45f), 12);
        made.Text = logo != null ? "MADE WITH" : "MADE WITH GODOT ENGINE";
        made.VerticalAlignment = VerticalAlignment.Center;
        engine.AddChild(made);
        if (logo != null)
            engine.AddChild(new TextureRect
            {
                Texture = logo, CustomMinimumSize = logo.GetSize(), Modulate = new Color(0.42f, 0.62f, 0.54f),
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        _titleGroup.AddChild(engine);

        _titleGroup.Modulate = new Color(1, 1, 1, 0);
        CreateTween().TweenProperty(_titleGroup, "modulate:a", 1f, 2.5).SetDelay(0.8);
        first?.CallDeferred(Control.MethodName.GrabFocus);
    }

    public void HideTitle(float seconds = 0.8f)
    {
        if (_titleGroup == null) return;
        var g = _titleGroup;
        _titleGroup = null;
        var tw = CreateTween();
        tw.TweenProperty(g, "modulate:a", 0f, seconds);
        tw.TweenCallback(Callable.From(g.QueueFree));
    }

    public void SetHold(string text, bool visible) { EnsureM3(); _hold.Text = text; _hold.Visible = visible; }

    public void ShowArm(bool on) { EnsureM3(); _armGroup.Visible = on; }
    public void SetArmInfo(string text) { EnsureM3(); _armInfo.Text = text; }

    public bool PanelVisible => _panel != null && _panel.Visible;

    /// <summary>Show a station panel. Options become numbered buttons; picking calls onPick(index).</summary>
    public void ShowPanel(string title, string body, System.Collections.Generic.IReadOnlyList<(string label, bool enabled)> options, System.Action<int> onPick)
    {
        EnsureM3();
        _panel.Visible = true;
        _panelTitle.Text = title;
        _panelBody.Text = body;
        foreach (var c in _panelButtons.GetChildren()) c.QueueFree();
        _panelKeys.Clear();
        int rows = 0;
        for (int i = 0; i < options.Count; i++)
        {
            int idx = i;
            if (options[i].label.StartsWith("## "))
            {
                // A section header, not an option.
                var h = MakeLabel(new Color(0.55f, 0.85f, 0.7f), 13);
                h.Text = options[i].label.Substring(3);
                if (i > 0) _panelButtons.AddChild(new HSeparator());
                _panelButtons.AddChild(h);
                rows++;
                continue;
            }
            _panelKeys.Add(i);
            rows++;
            string key = _panelKeys.Count <= 9 ? $"[{_panelKeys.Count}]" : "[  ]";
            var b = new Button
            {
                Text = $"{key}  {options[i].label}",
                Disabled = !options[i].enabled,
                Alignment = HorizontalAlignment.Left,
                FocusMode = Control.FocusModeEnum.None,
            };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.AddThemeColorOverride("font_color", Phosphor);
            b.AddThemeColorOverride("font_disabled_color", new Color(0.45f, 0.5f, 0.47f));
            b.Pressed += () => onPick(idx);
            _panelButtons.AddChild(b);
        }
        // Grow with the list up to most of the screen height, then scroll.
        float vh = GetViewport().GetVisibleRect().Size.Y;
        _panelScroll.CustomMinimumSize = new Vector2(530, Mathf.Min(rows * 31f, vh * 0.55f));
    }

    public void HidePanel() { if (_panel != null) _panel.Visible = false; }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_messageTime > 0f)
        {
            _messageTime -= dt;
            if (_messageTime < 1f) _message.Modulate = new Color(1, 1, 1, Mathf.Max(0f, _messageTime));
        }
        _warnBlink += dt;
        _warning.Visible = Mathf.PosMod(_warnBlink, 0.9f) < 0.6f;
    }
}
