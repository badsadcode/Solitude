using System.Collections.Generic;
using Godot;
using Solitude.Systems;

namespace Solitude;

/// <summary>
/// Voices on the radio (generated with Tom's NoorVox; see tools/voice/). Only ever through a
/// machine: the comms set, a pulled recorder. One line at a time from a queue, each shown as a
/// subtitle while it plays; the world ducks a little under it. The player character never speaks.
/// </summary>
public partial class Main
{
    private AudioStreamPlayer _voice;
    private readonly Queue<(AudioStream stream, string subtitle, float seconds, float gapAfter)> _voiceQueue = new();
    private float _voiceGap;

    private static readonly string[] DigitWords = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    private void EnsureVoice()
    {
        if (_voice != null) return;
        _voice = new AudioStreamPlayer { Name = "RadioVoice", Bus = "Master", VolumeDb = -2f };
        AddChild(_voice);
    }

    /// <summary>Queue a voiced line. subtitle null = no caption (used for clips inside a sequence).</summary>
    private bool QueueVoice(string id, string subtitle, float gapAfter = 0.35f, float showFor = 0f)
    {
        var line = VoiceLines.Get(id);
        if (line == null) return false;
        var stream = GD.Load<AudioStream>(line.File);
        if (stream == null) return false;
        EnsureVoice();
        _voiceQueue.Enqueue((stream, subtitle, showFor > 0f ? showFor : line.Seconds, gapAfter));
        return true;
    }

    private bool VoiceBusy => (_voice != null && _voice.Playing) || _voiceQueue.Count > 0;

    private void UpdateVoice(float dt)
    {
        if (_voice == null) return;
        if (_voice.Playing) return;
        if (_voiceGap > 0f) { _voiceGap -= dt; return; }
        if (_voiceQueue.Count == 0) return;
        var (stream, subtitle, seconds, gap) = _voiceQueue.Dequeue();
        _voice.Stream = stream;
        _voice.Play();
        _voiceGap = gap;
        if (subtitle != null) _hud.Message(subtitle, seconds + 1f);
        _audio.Duck(-5f, 0.4f);
        GetTree().CreateTimer(((AudioStreamWav)stream).GetLength() + 0.2).Timeout += () => { if (!VoiceBusy) _audio.Duck(0f, 1.2f); };
    }

    /// <summary>A wreck's loop on the comms set. first = the subtitle that goes in the ship's log.</summary>
    private bool PlayWreckLoop(WreckSite w, bool first)
    {
        if (w.Wrong) return PlayWrongLoop(first);
        if (w.VoiceKey == null || VoiceLines.Get(w.VoiceKey + "_loop") is not { } line) return false;
        string sub = first ? WreckDirectory.Loop(w) : $"\"{line.Caption}\"";
        return QueueVoice(w.VoiceKey + "_loop", sub, 2.5f);
    }

    /// <summary>
    /// The wrong call: a tired man's voice giving YOUR heading and depth, right now, then "we're fine".
    /// Strung together from single words, so the numbers are always the boat's own.
    /// </summary>
    private bool PlayWrongLoop(bool first)
    {
        if (VoiceLines.Get("wrong_open") == null) return false;
        int hdg = Mathf.RoundToInt(_sub.Heading) % 360;
        int dep = Mathf.RoundToInt(_sub.Depth);
        string hs = hdg.ToString("000"), ds = dep.ToString();
        var words = new List<string>();
        foreach (char c in hs) words.Add(DigitWords[c - '0']);
        string depthWords = string.Join(" ", System.Linq.Enumerable.Select(ds, c => DigitWords[c - '0']));
        string caption = $"Still here. Heading {string.Join(" ", words)}. Depth {depthWords} metres. We're fine. Someone will come down the lane.";
        string sub = first ? $"COMMS: a man's voice, tired, calm: \"{caption}\"" : $"\"{caption}\"";
        // The caption stays up for the whole sequence.
        float total = 0f;
        foreach (var id in new[] { "wrong_open", "wrong_heading", "wrong_depth", "wrong_metres", "wrong_close" }) total += VoiceLines.Get(id)?.Seconds ?? 0f;
        foreach (char c in hs + ds) total += (VoiceLines.Get($"digit_{c}")?.Seconds ?? 0f) + 0.08f;
        QueueVoice("wrong_open", sub, 0.5f, showFor: total + 1.5f);
        QueueVoice("wrong_heading", null, 0.15f);
        foreach (char c in hs) QueueVoice($"digit_{c}", null, 0.08f);
        QueueVoice("wrong_depth", null, 0.15f);
        foreach (char c in ds) QueueVoice($"digit_{c}", null, 0.08f);
        QueueVoice("wrong_metres", null, 0.6f);
        QueueVoice("wrong_close", null, 2.5f);
        return true;
    }

    /// <summary>A pulled recorder plays its last entries, one by one, with subtitles.</summary>
    private void PlayRecorder(WreckSite w)
    {
        foreach (var line in WreckDirectory.RecorderLines(w))
            QueueVoice(line.Id, $"\"{line.Caption}\"", 1.2f);
    }
}
