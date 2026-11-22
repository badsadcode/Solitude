using Godot;

namespace Solitude.UI;

/// <summary>Small cross-hair box showing yoke position (rudder X, planes Y) and ballast bar.</summary>
public partial class YokeGauge : Control
{
    public float Rudder, Planes, Ballast;
    private static readonly Color Ink = new(0.55f, 1f, 0.7f, 0.85f);
    private static readonly Color Dim = new(0.55f, 1f, 0.7f, 0.25f);

    public override void _Draw()
    {
        var s = Size;
        float box = s.Y;
        var r = new Rect2(0, 0, box, box);
        DrawRect(r, Dim, false, 1f);
        DrawLine(new Vector2(box / 2, 0), new Vector2(box / 2, box), Dim);
        DrawLine(new Vector2(0, box / 2), new Vector2(box, box / 2), Dim);
        var p = new Vector2(box / 2 + Rudder * box / 2 * 0.9f, box / 2 - Planes * box / 2 * 0.9f);
        DrawRect(new Rect2(p - new Vector2(3, 3), new Vector2(6, 6)), Ink);

        // Ballast bar to the right
        float bx = box + 10, bw = 10;
        DrawRect(new Rect2(bx, 0, bw, box), Dim, false, 1f);
        DrawLine(new Vector2(bx - 2, box / 2), new Vector2(bx + bw + 2, box / 2), Dim);
        float y = box / 2 - Ballast * box / 2;
        DrawRect(new Rect2(bx, Mathf.Min(y, box / 2), bw, Mathf.Abs(y - box / 2)), Ink);
    }

    public override void _Process(double delta) => QueueRedraw();
}
