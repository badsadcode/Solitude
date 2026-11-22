using System;
using Godot;

namespace Solitude.UI;

/// <summary>
/// What you see through the hard suit's faceplate: a dark rounded frame, a couple of glass glints,
/// and a strip of readouts along the bottom of the visor (O2, depth, bag, lamp, distance home).
/// </summary>
public partial class HelmetOverlay : Control
{
    public float Oxygen = 100f, Depth, HomeDistance, HomeBearing;   // bearing relative to view, degrees
    public int Bag, BagSize = 6;
    public bool Lamp;
    public float Fog;               // 0..1 visor condensation when panicking / low air
    public Vector2 Reticle;         // where the (lagging) harpoon points, window pixels
    public bool ReticleOn;
    public string Weapon = "";      // harpoon status line
    public bool WeaponReady;
    public float ZapFlash;          // 0..1 blue-white flash when the prod bites you

    private float _t;
    private static readonly Color Frame = new(0.01f, 0.012f, 0.012f, 1f);
    private static readonly Color Ink = new(0.55f, 1f, 0.75f, 0.9f);
    private static readonly Color Warn = new(1f, 0.35f, 0.2f, 0.95f);

    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 s = Size;
        Vector2 c = s / 2f;
        float rx = s.X * 0.52f, ry = s.Y * 0.56f;

        // Frame: everything outside a rounded oval is helmet.
        const int seg = 72;
        var outer = new Vector2[seg + 3];
        for (int i = 0; i <= seg; i++)
        {
            float a = Mathf.Tau * i / seg;
            float sq = 1f + 0.08f * MathF.Pow(MathF.Abs(MathF.Cos(2f * a)), 4f); // a squarer oval
            var p = c + new Vector2(MathF.Cos(a) * rx * sq, MathF.Sin(a) * ry * sq);
            p.X = Mathf.Clamp(p.X, -10, s.X + 10); p.Y = Mathf.Clamp(p.Y, -10, s.Y + 10);
            outer[i] = p;
        }
        // Draw the frame as quads between the oval and the screen edge.
        for (int i = 0; i < seg; i++)
        {
            Vector2 a = outer[i], b = outer[i + 1];
            Vector2 ea = c + (a - c).Normalized() * s.Length(), eb = c + (b - c).Normalized() * s.Length();
            DrawColoredPolygon(new[] { a, b, eb, ea }, Frame);
        }
        // Soft inner rim
        for (int k = 0; k < 6; k++)
        {
            var rim = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++) rim[i] = c + (outer[i] - c) * (1f - k * 0.012f);
            DrawPolyline(rim, new Color(0, 0, 0, 0.35f - k * 0.05f), 6f);
        }
        // Glass glints
        DrawArc(c + new Vector2(-rx * 0.35f, -ry * 0.45f), rx * 0.5f, -2.6f, -2.1f, 16, new Color(1, 1, 1, 0.07f), 3f);
        DrawArc(c + new Vector2(rx * 0.42f, ry * 0.3f), rx * 0.3f, 0.2f, 0.6f, 12, new Color(1, 1, 1, 0.05f), 2f);
        // Condensation
        if (Fog > 0.01f)
        {
            for (int i = 0; i < 40; i++)
            {
                float a = (i * 2.399f) % Mathf.Tau, r = 0.55f + (i % 7) * 0.06f;
                var p = c + new Vector2(MathF.Cos(a) * rx * r, MathF.Sin(a) * ry * r);
                DrawCircle(p, 18f + (i % 5) * 9f, new Color(0.8f, 0.85f, 0.85f, 0.05f * Fog));
            }
        }

        // Harpoon reticle: a small bracket where the gun really points (it drifts as the gun lags).
        if (ReticleOn)
        {
            Color rc = WeaponReady ? Ink : new Color(Ink, 0.35f);
            Vector2 r = Reticle;
            DrawLine(r + new Vector2(-9, -9), r + new Vector2(-4, -9), rc, 1.5f); DrawLine(r + new Vector2(-9, -9), r + new Vector2(-9, -4), rc, 1.5f);
            DrawLine(r + new Vector2(9, -9), r + new Vector2(4, -9), rc, 1.5f); DrawLine(r + new Vector2(9, -9), r + new Vector2(9, -4), rc, 1.5f);
            DrawLine(r + new Vector2(-9, 9), r + new Vector2(-4, 9), rc, 1.5f); DrawLine(r + new Vector2(-9, 9), r + new Vector2(-9, 4), rc, 1.5f);
            DrawLine(r + new Vector2(9, 9), r + new Vector2(4, 9), rc, 1.5f); DrawLine(r + new Vector2(9, 9), r + new Vector2(9, 4), rc, 1.5f);
            DrawCircle(r, 1.5f, rc);
        }
        if (ZapFlash > 0.01f) DrawRect(new Rect2(Vector2.Zero, s), new Color(0.7f, 0.85f, 1f, ZapFlash * 0.5f));

        // Readouts along the bottom of the visor
        var font = ThemeDB.FallbackFont;
        float y = c.Y + ry * 0.62f;   // inside the visor, clear of the frame
        bool low = Oxygen < 25f;
        Color o2c = low && MathF.Sin(_t * 8f) > 0f ? Warn : (Oxygen < 50f ? new Color(1f, 0.75f, 0.3f, 0.9f) : Ink);
        float barW = 180f;
        var bar = new Rect2(c.X - barW / 2f, y, barW, 8f);
        DrawRect(bar, new Color(Ink, 0.2f), false, 1f);
        DrawRect(new Rect2(bar.Position, new Vector2(barW * Mathf.Clamp(Oxygen / 100f, 0f, 1f), 8f)), o2c);
        DrawString(font, new Vector2(c.X - barW / 2f, y - 6f), $"O2 {Oxygen:0}%", HorizontalAlignment.Left, -1, 14, o2c);
        DrawString(font, new Vector2(c.X + barW / 2f - 90f, y - 6f), $"{Depth:0} m", HorizontalAlignment.Right, 90, 14, Ink);
        DrawString(font, new Vector2(c.X - barW / 2f, y + 26f), $"BAG {Bag}/{BagSize}   LAMP {(Lamp ? "ON" : "OFF")} [F]", HorizontalAlignment.Left, -1, 13, Ink);
        if (Weapon.Length > 0)
            DrawString(font, new Vector2(c.X - barW / 2f, y + 44f), $"{Weapon}  [Q] swap", HorizontalAlignment.Left, -1, 13, WeaponReady ? Ink : new Color(Ink, 0.5f));

        // Homing chevron toward the boat's hatch
        string home = HomeDistance > 999f ? "—" : $"{HomeDistance:0} m";
        float ang = Mathf.DegToRad(HomeBearing);
        Vector2 dir = new(MathF.Sin(ang), -MathF.Cos(ang));
        Vector2 hp = c + new Vector2(0, -ry * 0.7f);
        DrawString(font, hp + new Vector2(-40f, 0), $"HATCH {home}", HorizontalAlignment.Center, 80, 13, Ink);
        Vector2 tip = hp + new Vector2(0, 14f) + dir * 16f;
        DrawLine(hp + new Vector2(0, 14f), tip, Ink, 2f);
        DrawCircle(tip, 3f, Ink);
    }
}
