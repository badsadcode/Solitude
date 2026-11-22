using Godot;

namespace Solitude.UI;

/// <summary>The helm HUD's copy of the six-lamp proximity panel (for when you're flying from an outside view).</summary>
public partial class ProxStrip : Control
{
    public readonly Color[] Col = new Color[6];
    public readonly float[] Energy = new float[6];
    /// <summary>On the camera overlay: the lamp of the feed being watched gets a bright frame (-1 = none).</summary>
    public int Selected = -1;
    private static readonly Color Dim = new(0.55f, 1f, 0.7f, 0.25f);
    private static readonly Color Ink = new(0.55f, 1f, 0.7f, 0.6f);
    private static readonly string[] Short = { "BOW", "PRT", "STB", "KEL", "STN", "MST" };

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        for (int i = 0; i < 6; i++)
        {
            var r = new Rect2(i * 30, 0, 24, 12);
            float e = Mathf.Clamp(Energy[i] / 3f, 0f, 1f);
            if (e > 0.02f) DrawRect(r, new Color(Col[i], 0.25f + e * 0.75f));
            DrawRect(r, i == Selected ? Ink : Dim, false, i == Selected ? 2f : 1f);
            if (font != null) DrawString(font, new Vector2(i * 30, 27), Short[i], HorizontalAlignment.Left, -1, 10, Ink);
        }
    }

    public override void _Process(double delta) => QueueRedraw();
}
