using System;
using System.Collections.Generic;
using Godot;

namespace Solitude.UI;

/// <summary>
/// The ship's log terminal (and, in paper colours, the carried journal). Drawn by hand into a
/// 640x480 SubViewport so the same picture glows on the galley wall and fills the screen when you
/// sit down at it.
///
/// Tabs are plain pages of lines. One special tab ("NOTES") is a list on the left and the selected
/// note on the right. Keys: A/D tab, W/S scroll (or pick a note), wheel scrolls, click works too.
/// The screen knows nothing about the game: Main gives it a function per tab that returns lines.
/// </summary>
public partial class TerminalScreen : Control
{
    public const int W = 640, H = 480;

    public readonly struct Line
    {
        public readonly string Text;
        public readonly int Style;   // 0 normal, 1 header, 2 dim, 3 alert
        public Line(string text, int style = 0) { Text = text; Style = style; }
    }

    public sealed class Tab
    {
        public string Name;
        public Func<List<Line>> Lines;     // a page of lines
        public bool Notes;                 // the list + reader layout
        public bool StickToBottom;         // the log: newest at the bottom, opens scrolled down
    }

    public string Title = "SHIP'S LOG";
    public Func<string> Clock;
    /// <summary>Notes for the NOTES tab: (title, where, body).</summary>
    public Func<List<(string title, string where, string body)>> NotesSource;
    public List<Tab> Tabs = new();
    public bool Paper;
    /// <summary>Keyboard hints along the bottom (empty on the wall screen).</summary>
    public string Hint = "";

    public int Current { get; private set; }
    private int _scroll, _note, _noteScroll;
    private readonly List<(Rect2 r, int tab)> _tabRects = new();
    private readonly List<(Rect2 r, int note)> _noteRects = new();
    private Font _font;
    private const int Fs = 15, Lh = 18;
    private float _blink;

    private Color Bg => Paper ? new Color(0.82f, 0.77f, 0.64f) : new Color(0.02f, 0.05f, 0.035f);
    private Color Ink => Paper ? new Color(0.17f, 0.12f, 0.09f) : new Color(0.55f, 1f, 0.7f);
    private Color Dim => Paper ? new Color(0.45f, 0.38f, 0.3f) : new Color(0.3f, 0.6f, 0.42f);
    private Color Alert => Paper ? new Color(0.6f, 0.1f, 0.05f) : new Color(1f, 0.55f, 0.25f);

    public override void _Ready()
    {
        Size = new Vector2(W, H);
        _font = new SystemFont
        {
            FontNames = Paper ? new[] { "Courier New", "Consolas", "monospace" } : new[] { "Consolas", "Lucida Console", "monospace" },
            Antialiasing = Paper ? TextServer.FontAntialiasing.Gray : TextServer.FontAntialiasing.None,
        };
    }

    public void SetTab(int i)
    {
        if (Tabs.Count == 0) return;
        Current = ((i % Tabs.Count) + Tabs.Count) % Tabs.Count;
        _scroll = 0; _noteScroll = 0;
        QueueRedraw();
    }

    public void NextTab(int dir) => SetTab(Current + dir);

    /// <summary>W/S: scroll a page, or step through the notes list.</summary>
    public void Step(int dir)
    {
        if (Tabs.Count == 0) return;
        if (Tabs[Current].Notes)
        {
            int n = NotesSource?.Invoke()?.Count ?? 0;
            if (n > 0) { _note = Math.Clamp(_note + dir, 0, n - 1); _noteScroll = 0; }
        }
        else _scroll += dir * (Tabs[Current].StickToBottom ? -1 : 1);
        QueueRedraw();
    }

    /// <summary>Mouse wheel: scroll the text (on the NOTES tab, the open note).</summary>
    public void Wheel(int dir)
    {
        if (Tabs.Count == 0) return;
        if (Tabs[Current].Notes) _noteScroll = Math.Max(0, _noteScroll + dir * 2);
        else _scroll += dir * 3 * (Tabs[Current].StickToBottom ? -1 : 1);
        QueueRedraw();
    }

    /// <summary>A click in screen pixels (0..640, 0..480).</summary>
    public void Click(Vector2 p)
    {
        foreach (var (r, t) in _tabRects) if (r.HasPoint(p)) { SetTab(t); return; }
        foreach (var (r, n) in _noteRects) if (r.HasPoint(p)) { _note = n; _noteScroll = 0; QueueRedraw(); return; }
    }

    public override void _Process(double delta)
    {
        _blink += (float)delta;
        QueueRedraw();
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        if (_font == null) return;
        DrawRect(new Rect2(0, 0, W, H), Bg);
        if (Paper)
        {
            // Ruled paper: faint lines and a margin.
            for (int y = 92; y < H - 30; y += Lh) DrawLine(new Vector2(16, y + 4), new Vector2(W - 16, y + 4), new Color(Dim, 0.18f));
            DrawLine(new Vector2(40, 60), new Vector2(40, H - 20), new Color(0.7f, 0.25f, 0.2f, 0.35f));
        }

        // Header: title left, clock right, a rule underneath.
        DrawString(_font, new Vector2(16, 26), Title, HorizontalAlignment.Left, -1, Fs + 2, Ink);
        if (Clock != null) DrawString(_font, new Vector2(16, 26), Clock(), HorizontalAlignment.Right, W - 32, Fs, Dim);
        DrawLine(new Vector2(16, 34), new Vector2(W - 16, 34), Dim);

        // Tabs.
        _tabRects.Clear();
        float x = 16;
        for (int i = 0; i < Tabs.Count; i++)
        {
            string label = $" {i + 1} {Tabs[i].Name} ";
            float w = _font.GetStringSize(label, HorizontalAlignment.Left, -1, Fs).X;
            var r = new Rect2(x, 42, w, 22);
            if (i == Current) { DrawRect(r, Ink); DrawString(_font, new Vector2(x, 58), label, HorizontalAlignment.Left, -1, Fs, Bg); }
            else { DrawRect(r, Dim, false, 1f); DrawString(_font, new Vector2(x, 58), label, HorizontalAlignment.Left, -1, Fs, Dim); }
            _tabRects.Add((r, i));
            x += w + 8;
        }

        _noteRects.Clear();
        if (Tabs.Count == 0) return;
        var top = 76f;
        var bottom = H - (string.IsNullOrEmpty(Hint) ? 14f : 34f);
        if (Tabs[Current].Notes) DrawNotes(top, bottom);
        else DrawPage(Tabs[Current], top, bottom);

        if (!string.IsNullOrEmpty(Hint))
        {
            DrawLine(new Vector2(16, H - 28), new Vector2(W - 16, H - 28), Dim);
            DrawString(_font, new Vector2(16, H - 10), Hint, HorizontalAlignment.Left, W - 32, Fs - 2, Dim);
        }
    }

    private void DrawPage(Tab tab, float top, float bottom)
    {
        var wrapped = new List<(string, int)>();
        foreach (var l in tab.Lines?.Invoke() ?? new List<Line>())
            foreach (var w in Wrap(l.Text, W - 48 - (Paper ? 30 : 0))) wrapped.Add((w, l.Style));
        int rows = (int)((bottom - top) / Lh);
        int maxScroll = Math.Max(0, wrapped.Count - rows);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        int first = tab.StickToBottom ? Math.Max(0, wrapped.Count - rows - _scroll) : _scroll;
        float left = Paper ? 48 : 20;
        for (int i = 0; i < rows && first + i < wrapped.Count; i++)
        {
            var (text, style) = wrapped[first + i];
            Color c = style switch { 1 => Ink, 2 => Dim, 3 => Alert, _ => Ink };
            if (style == 1 && !Paper) DrawRect(new Rect2(left - 4, top + i * Lh + 2, W - left - 12, Lh), new Color(Ink, 0.12f));
            DrawString(_font, new Vector2(left, top + 14 + i * Lh), text, HorizontalAlignment.Left, -1, Fs, c);
        }
        // Scroll marks.
        if (maxScroll > 0)
        {
            float h = bottom - top;
            float pos = tab.StickToBottom ? 1f - (float)_scroll / maxScroll : (float)_scroll / maxScroll;
            DrawRect(new Rect2(W - 10, top, 2, h), new Color(Dim, 0.4f));
            DrawRect(new Rect2(W - 11, top + pos * (h - 20), 4, 20), Dim);
        }
        // A blinking cursor under the last log line.
        if (tab.StickToBottom && _scroll == 0 && !Paper && (int)(_blink * 2f) % 2 == 0)
        {
            int shown = Math.Min(rows, wrapped.Count);
            if (shown < rows) DrawRect(new Rect2(left, top + 4 + shown * Lh, 9, 14), Ink);
        }
    }

    private void DrawNotes(float top, float bottom)
    {
        var notes = NotesSource?.Invoke() ?? new List<(string, string, string)>();
        float left = Paper ? 48 : 20, listW = 190;
        if (notes.Count == 0)
        {
            DrawString(_font, new Vector2(left, top + 14), "No notes yet. The stations are full of them.", HorizontalAlignment.Left, -1, Fs, Dim);
            return;
        }
        _note = Math.Clamp(_note, 0, notes.Count - 1);
        int rows = (int)((bottom - top) / Lh);
        int first = Math.Clamp(_note - rows / 2, 0, Math.Max(0, notes.Count - rows));
        for (int i = 0; i < rows && first + i < notes.Count; i++)
        {
            int n = first + i;
            string t = Clip(notes[n].title, listW - 8);
            var r = new Rect2(left - 4, top + i * Lh + 2, listW, Lh);
            if (n == _note) { DrawRect(r, Ink); DrawString(_font, new Vector2(left, top + 14 + i * Lh), t, HorizontalAlignment.Left, -1, Fs, Bg); }
            else DrawString(_font, new Vector2(left, top + 14 + i * Lh), t, HorizontalAlignment.Left, -1, Fs, Ink);
            _noteRects.Add((r, n));
        }
        float bx = left + listW + 14;
        DrawLine(new Vector2(bx - 8, top), new Vector2(bx - 8, bottom), Dim);
        var (title, where, body) = notes[_note];
        var lines = new List<(string, Color)>();
        foreach (var w in Wrap(title, W - bx - 16)) lines.Add((w, Ink));
        foreach (var w in Wrap(where, W - bx - 16)) lines.Add((w, Dim));
        lines.Add(("", Ink));
        foreach (var para in body.Split('\n'))
            foreach (var w in Wrap(para, W - bx - 16)) lines.Add((w, Ink));
        int brows = (int)((bottom - top) / Lh);
        _noteScroll = Math.Clamp(_noteScroll, 0, Math.Max(0, lines.Count - brows));
        for (int i = 0; i < brows && _noteScroll + i < lines.Count; i++)
            DrawString(_font, new Vector2(bx, top + 14 + i * Lh), lines[_noteScroll + i].Item1, HorizontalAlignment.Left, -1, Fs, lines[_noteScroll + i].Item2);
    }

    private string Clip(string s, float width)
    {
        if (_font.GetStringSize(s, HorizontalAlignment.Left, -1, Fs).X <= width) return s;
        while (s.Length > 1 && _font.GetStringSize(s + "…", HorizontalAlignment.Left, -1, Fs).X > width) s = s[..^1];
        return s + "…";
    }

    private List<string> Wrap(string text, float width)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(text)) { outp.Add(""); return outp; }
        // Continuation lines of a log entry hang under its text, not under its timestamp.
        string indent = "";
        int bar = text.IndexOf("  ", StringComparison.Ordinal);
        if (text.Length > 10 && char.IsDigit(text[0]) && bar > 0 && bar < 24) indent = new string(' ', bar + 2);
        var words = text.Split(' ');
        string line = "";
        foreach (var word in words)
        {
            string tryLine = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && _font.GetStringSize(tryLine, HorizontalAlignment.Left, -1, Fs).X > width)
            {
                outp.Add(line);
                line = indent + word;
            }
            else line = tryLine;
        }
        outp.Add(line);
        return outp;
    }
}
