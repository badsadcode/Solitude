using System;
using Godot;
using Solitude.Systems;

namespace Solitude.Sonar;

/// <summary>
/// The hydrophone console's CRT (256 × 256, rendered into its own viewport like the sonar scope):
/// a bearing-time waterfall on top (bow in the middle, astern at both edges, newest row at the top),
/// the listening beam as a bracket, and the operator's readouts underneath.
/// </summary>
public partial class HydrophoneDisplay : Control
{
    public Hydrophone Phone;
    /// <summary>Set by Main every frame: beam bearing, what's in the beam, recording progress.</summary>
    public float Beam;
    public string Line1 = "", Line2 = "", Line3 = "", Line4 = "";
    public float RecordProgress = -1f;   // 0..1 while recording
    public bool SelfNoisy;

    private Image _img;
    private ImageTexture _tex;
    private int _seen = -1;
    private float _t;

    private static readonly Color Ink = new(0.45f, 1f, 0.6f);
    private static readonly Color Dim = new(0.25f, 0.6f, 0.35f);
    private static readonly Color Warn = new(1f, 0.6f, 0.25f);
    private const float Top = 14f, WfHeight = 150f;

    public override void _Ready()
    {
        _img = Image.CreateEmpty(Hydrophone.Bins, Hydrophone.Rows, false, Image.Format.Rgb8);
        _tex = ImageTexture.CreateFromImage(_img);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (Phone != null && Phone.Version != _seen)
        {
            _seen = Phone.Version;
            for (int r = 0; r < Hydrophone.Rows; r++)
                for (int b = 0; b < Hydrophone.Bins; b++)
                {
                    float v = Mathf.Clamp(Phone.Waterfall[r, b], 0f, 1.2f);
                    // Phosphor ramp: black → deep green → bright green → near white at the top.
                    float g = Mathf.Clamp(v * 1.1f, 0f, 1f);
                    var c = new Color(g * g * 0.55f, g, g * 0.55f) * (1f - r / (float)Hydrophone.Rows * 0.35f);
                    _img.SetPixel(b, r, c);
                }
            _tex.Update(_img);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.01f, 0.03f, 0.02f));
        float w = Size.X;
        DrawString(font, new Vector2(4, 11), "HYDROPHONE  ·  BEARING / TIME", HorizontalAlignment.Left, -1, 10, Dim);
        DrawString(font, new Vector2(w - 60, 11), Phone == null ? "" : $"BAND {Phone.Filter.ToString().ToUpperInvariant()}", HorizontalAlignment.Left, -1, 10, Ink);

        var wf = new Rect2(0, Top, w, WfHeight);
        DrawTextureRect(_tex, wf, false);
        // Bearing ticks: 180 (port astern), 90 port, 0 bow, 90 stbd, 180.
        for (int i = 0; i <= 4; i++)
        {
            float x = w * i / 4f;
            DrawLine(new Vector2(x, Top + WfHeight), new Vector2(x, Top + WfHeight + 4), Dim, 1f);
            string lbl = i switch { 0 => "180", 1 => "P90", 2 => "000", 3 => "S90", _ => "180" };
            DrawString(font, new Vector2(Mathf.Clamp(x - 10, 0, w - 22), Top + WfHeight + 13), lbl, HorizontalAlignment.Left, -1, 9, Dim);
        }
        // The beam: a bracket at the steered bearing.
        float bx = (Hydrophone.Wrap(Beam) + 180f) / 360f * w;
        var beamCol = new Color(1f, 0.85f, 0.4f, 0.85f);
        DrawLine(new Vector2(bx - 5, Top), new Vector2(bx - 5, Top + WfHeight), new Color(beamCol, 0.35f), 1f);
        DrawLine(new Vector2(bx + 5, Top), new Vector2(bx + 5, Top + WfHeight), new Color(beamCol, 0.35f), 1f);
        DrawLine(new Vector2(bx - 7, Top - 2), new Vector2(bx + 7, Top - 2), beamCol, 2f);

        float y = Top + WfHeight + 28f;
        DrawString(font, new Vector2(4, y), Line1, HorizontalAlignment.Left, -1, 11, Ink);
        DrawString(font, new Vector2(4, y + 14), Line2, HorizontalAlignment.Left, -1, 10, Ink);
        DrawString(font, new Vector2(4, y + 28), Line3, HorizontalAlignment.Left, -1, 10, Line3.StartsWith("MATCH") || Line3.StartsWith("ID") ? new Color(1f, 0.85f, 0.4f) : Dim);
        DrawString(font, new Vector2(4, y + 42), Line4, HorizontalAlignment.Left, -1, 9, Dim);
        if (SelfNoisy && MathF.Sin(_t * 5f) > 0f)
            DrawString(font, new Vector2(w - 92, y), "OWN NOISE HIGH", HorizontalAlignment.Left, -1, 9, Warn);
        if (RecordProgress >= 0f)
        {
            DrawRect(new Rect2(w - 70, y + 34, 64, 6), new Color(Ink, 0.25f), false, 1f);
            DrawRect(new Rect2(w - 70, y + 34, 64 * RecordProgress, 6), new Color(1f, 0.3f, 0.25f));
            if (MathF.Sin(_t * 8f) > 0f) DrawCircle(new Vector2(w - 78, y + 37), 3f, new Color(1f, 0.25f, 0.2f));
        }
    }
}
