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
        _feedName.Position = new Vector2(32, 24);
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

    public void SetPrompt(string text) => _prompt.Text = text ?? "";
    public void SetCrosshair(bool on) => _crosshair.Visible = on;
    public void SetHelp(string text) => _help.Text = text ?? "";

    public void Message(string text, float seconds = 5f)
    {
        _message.Text = text;
        _messageTime = seconds;
        _message.Modulate = Colors.White;
    }

    public void ShowHelm(bool on) => _helmGroup.Visible = on;

    public void ShowFeed(Texture2D tex, string name, float signal, string hint = "[1-6] switch   [F] close")
    {
        bool on = tex != null;
        _feedOverlay.Visible = on;
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
        sb.AppendLine($"HULL     {sub.Hull,6:0}%    {(sub.LightsOn ? "FLOODS ON" : "FLOODS OFF")}");
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
