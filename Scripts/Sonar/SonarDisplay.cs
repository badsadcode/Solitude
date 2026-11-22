using System;
using Godot;
using Solitude.Sub;

namespace Solitude.Sonar;

/// <summary>
/// Heading-up PPI (plan position indicator) scope, drawn in 2D into a small SubViewport
/// whose texture feeds the sonar console CRT (and the fullscreen overlay).
/// Green = seabed returns, amber = terrain at/above the keel, pale = unclassified.
/// </summary>
public partial class SonarDisplay : Control
{
    public SonarSystem Sonar;
    public Submarine Sub;

    private static readonly Color Bg = new(0.01f, 0.035f, 0.02f);
    private static readonly Color Grid = new(0.15f, 0.55f, 0.3f, 0.35f);
    private static readonly Color Green = new(0.35f, 1f, 0.55f);
    private static readonly Color Amber = new(1f, 0.7f, 0.2f);
    private static readonly Color Ghost = new(0.85f, 0.95f, 1f);
    private static readonly Color Text = new(0.45f, 1f, 0.6f, 0.9f);

    private readonly Random _rng = new();
    private float _sweep;

    public override void _Process(double delta)
    {
        _sweep = (_sweep + (float)delta * 0.6f) % Mathf.Tau;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Sonar == null || Sub == null) return;
        Vector2 size = Size;
        Vector2 c = size / 2f;
        float R = MathF.Min(size.X, size.Y) * 0.46f;
        float range = Sonar.Range;
        float scale = R / range;
        var font = ThemeDB.FallbackFont;
        double now = SonarSystem.Now;

        DrawRect(new Rect2(Vector2.Zero, size), Bg);

        // Rings and bearing ticks
        for (int i = 1; i <= 4; i++) DrawArc(c, R * i / 4f, 0, Mathf.Tau, 64, Grid, 1f);
        float hdg = Mathf.DegToRad(Sub.Heading);
        for (int deg = 0; deg < 360; deg += 30)
        {
            float a = Mathf.DegToRad(deg) - hdg;
            var d = new Vector2(MathF.Sin(a), -MathF.Cos(a));
            DrawLine(c + d * (R - 5f), c + d * R, Grid, 1f);
        }
        DrawLine(c - new Vector2(0, R), c + new Vector2(0, R), new Color(Grid, 0.15f), 1f);
        DrawLine(c - new Vector2(R, 0), c + new Vector2(R, 0), new Color(Grid, 0.15f), 1f);
        // North marker
        {
            float a = -hdg;
            var d = new Vector2(MathF.Sin(a), -MathF.Cos(a));
            DrawString(font, c + d * (R + 2f) - new Vector2(4, -4), "N", HorizontalAlignment.Left, -1, 10, Text);
        }

        // Noise floor shimmer and passive sweep line
        for (int i = 0; i < 40; i++)
        {
            float a = (float)_rng.NextDouble() * Mathf.Tau, r = MathF.Sqrt((float)_rng.NextDouble()) * R;
            DrawRect(new Rect2(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r, Vector2.One), new Color(Green, 0.12f));
        }
        var sw = new Vector2(MathF.Sin(_sweep), -MathF.Cos(_sweep));
        DrawLine(c, c + sw * R, new Color(Green, 0.18f), 1f);

        // Contacts, rotated into heading-up space
        Vector3 sp = Sub.GlobalPosition;
        Vector2 fwd = new(MathF.Sin(hdg), -MathF.Cos(hdg));
        Vector2 right = new(MathF.Cos(hdg), MathF.Sin(hdg));
        foreach (var k in Sonar.Contacts)
        {
            if (now < k.RevealAt) continue;
            float age = (float)(now - k.RevealAt);
            float alpha = k.Strength * MathF.Exp(-age / (k.Ghost ? 2.5f : 6f));
            if (alpha < 0.03f) continue;
            Vector2 rel = k.World - new Vector2(sp.X, sp.Z);
            Vector2 p = c + new Vector2(rel.Dot(right), -rel.Dot(fwd)) * scale;
            if ((p - c).Length() > R) continue;
            Color col = k.Ghost ? Ghost : k.Danger ? Amber : Green;
            float px = k.Strength > 0.6f ? 2f : 1f;
            DrawRect(new Rect2(p - Vector2.One * px * 0.5f, Vector2.One * px), new Color(col, alpha));
        }

        // Expanding wavefront (centred where the ping was fired)
        if (Sonar.WaveActive)
        {
            Vector2 rel = new Vector2(Sonar.PingOrigin.X - sp.X, Sonar.PingOrigin.Z - sp.Z);
            Vector2 o = c + new Vector2(rel.Dot(right), -rel.Dot(fwd)) * scale;
            float fr = Sonar.FrontRadius * scale;
            if (fr < R * 1.05f) DrawArc(o, fr, 0, Mathf.Tau, 96, new Color(Green, 0.7f), 1.5f);
        }

        // Own ship
        DrawColoredPolygon(new[] { c + new Vector2(0, -5), c + new Vector2(3.5f, 4), c + new Vector2(-3.5f, 4) }, new Color(Green, 0.9f));

        // Readouts
        DrawString(font, new Vector2(6, 14), $"RNG {range:0}M", HorizontalAlignment.Left, -1, 11, Text);
        DrawString(font, new Vector2(size.X - 62, 14), $"HDG {Mathf.RoundToInt(Sub.Heading) % 360:000}", HorizontalAlignment.Left, -1, 11, Text);
        string status = Sonar.RechargeLeft > 0 ? $"CHG {Sonar.RechargeLeft:0.0}" : "PING RDY";
        DrawString(font, new Vector2(6, size.Y - 6), status, HorizontalAlignment.Left, -1, 11, Sonar.RechargeLeft > 0 ? Amber : Text);
        DrawString(font, new Vector2(size.X - 70, size.Y - 6), $"DPT {Sub.Depth:0}", HorizontalAlignment.Left, -1, 11, Text);
    }
}
