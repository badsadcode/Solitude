using System;
using Godot;

namespace Solitude.UI;

/// <summary>
/// What you see from a hiding place. In a locker: darkness with a few horizontal slits of light
/// (the door's vents), slightly wobbling with your breathing. Under a desk: the desk top as a black
/// band above, the room as a low strip below. Plus a small breath gauge while you hold it.
/// </summary>
public partial class HideOverlay : Control
{
    public bool Desk;
    public float Breath01 = 1f;      // remaining held breath
    public bool Holding;
    public float Fear;               // 0..1: something is close

    private float _t;
    private static readonly Color Black = new(0.005f, 0.006f, 0.006f, 1f);

    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 s = Size;
        float breathe = Holding ? 0f : MathF.Sin(_t * (1.4f + Fear * 2.5f)) * 3f;
        if (Desk)
        {
            // The underside of the desk top, and darkness at the sides.
            float top = s.Y * 0.42f + breathe;
            DrawRect(new Rect2(0, 0, s.X, top), Black);
            DrawRect(new Rect2(0, top - 6f, s.X, 6f), new Color(0.06f, 0.055f, 0.05f, 1f));
            DrawRect(new Rect2(0, top, s.X * 0.12f, s.Y - top), new Color(0f, 0f, 0f, 0.85f));
            DrawRect(new Rect2(s.X * 0.88f, top, s.X * 0.12f, s.Y - top), new Color(0f, 0f, 0f, 0.85f));
        }
        else
        {
            // Locker door: solid dark with five vent slits across the middle.
            float slitH = s.Y * 0.035f, gap = s.Y * 0.075f;
            float y0 = s.Y * 0.36f + breathe;
            float x0 = s.X * 0.18f, x1 = s.X * 0.82f;
            float y = 0f;
            for (int i = 0; i < 5; i++)
            {
                float sy = y0 + i * gap;
                DrawRect(new Rect2(0, y, s.X, sy - y), Black);                    // door between slits
                DrawRect(new Rect2(0, sy, x0, slitH), Black);                     // slit ends
                DrawRect(new Rect2(x1, sy, s.X - x1, slitH), Black);
                DrawRect(new Rect2(x0, sy, x1 - x0, 2f), new Color(0.1f, 0.1f, 0.1f, 1f)); // the louvre's lip
                y = sy + slitH;
            }
            DrawRect(new Rect2(0, y, s.X, s.Y - y), Black);
        }
        // Holding your breath: a thin gauge, going red as it runs out.
        if (Holding || Breath01 < 0.999f)
        {
            float w = 160f;
            var r = new Rect2(s.X / 2f - w / 2f, s.Y - 70f, w, 4f);
            DrawRect(r, new Color(0.5f, 0.6f, 0.55f, 0.25f), false, 1f);
            var c = Breath01 > 0.35f ? new Color(0.6f, 0.85f, 0.7f, 0.8f) : new Color(1f, 0.35f, 0.25f, 0.9f);
            DrawRect(new Rect2(r.Position, new Vector2(w * Breath01, 4f)), c);
        }
    }
}
