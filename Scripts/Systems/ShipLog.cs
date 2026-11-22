using System;
using System.Collections.Generic;

namespace Solitude.Systems;

/// <summary>
/// The boat's memory: a running log of everything that happened on this dive, and every note the
/// player has read on the stations. Shown on the ship's log terminal (galley wall) and, the notes
/// part, in the carried journal. Pure data, saved with the game.
/// </summary>
public sealed class ShipLog
{
    public sealed class Entry
    {
        public float T;        // dive clock, seconds
        public float Depth;
        public string Text;
    }

    public sealed class Note
    {
        public string Title, Body, Where;
        public float T;
    }

    public readonly List<Entry> Entries = new();
    public readonly List<Note> Notes = new();
    /// <summary>Seconds since the dive began (saved; drives the timestamps).</summary>
    public float Clock;

    public const int MaxEntries = 400;

    public event Action Changed;

    public void Add(string text, float depth)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // The same line twice within a minute is one event (e.g. two knocks in a row).
        if (Entries.Count > 0 && Entries[^1].Text == text && Clock - Entries[^1].T < 60f) return;
        Entries.Add(new Entry { T = Clock, Depth = depth, Text = text });
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>Returns false if this note was already in the journal.</summary>
    public bool AddNote(string title, string body, string where)
    {
        foreach (var n in Notes) if (n.Title == title && n.Where == where) return false;
        Notes.Add(new Note { Title = title, Body = body, Where = where, T = Clock });
        Changed?.Invoke();
        return true;
    }

    /// <summary>Add a note, or replace the body of the one with the same title and place (a running transcript).</summary>
    public void UpsertNote(string title, string body, string where)
    {
        foreach (var n in Notes) if (n.Title == title && n.Where == where) { n.Body = body; Changed?.Invoke(); return; }
        Notes.Add(new Note { Title = title, Body = body, Where = where, T = Clock });
        Changed?.Invoke();
    }

    /// <summary>Dive clock as hh:mm:ss.</summary>
    public static string Stamp(float t)
    {
        int s = (int)t;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }

    // ---------------------------------------------------------------- save

    public List<string> SaveEntries()
    {
        var l = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var e in Entries) l.Add($"{e.T.ToString("0.0", inv)}|{e.Depth.ToString("0", inv)}|{e.Text}");
        return l;
    }

    public List<string> SaveNotes()
    {
        var l = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var n in Notes) l.Add($"{n.T.ToString("0.0", inv)}|{Esc(n.Where)}|{Esc(n.Title)}|{Esc(n.Body)}");
        return l;
    }

    public void Load(float clock, List<string> entries, List<string> notes)
    {
        Clock = clock;
        Entries.Clear(); Notes.Clear();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (entries != null)
            foreach (var s in entries)
            {
                var p = s.Split('|', 3);
                if (p.Length < 3) continue;
                float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out float t);
                float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out float d);
                Entries.Add(new Entry { T = t, Depth = d, Text = p[2] });
            }
        if (notes != null)
            foreach (var s in notes)
            {
                var p = s.Split('|', 4);
                if (p.Length < 4) continue;
                float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out float t);
                Notes.Add(new Note { T = t, Where = Un(p[1]), Title = Un(p[2]), Body = Un(p[3]) });
            }
        Changed?.Invoke();
    }

    private static string Esc(string s) => (s ?? "").Replace("|", "│");
    private static string Un(string s) => s.Replace("│", "|");
}
