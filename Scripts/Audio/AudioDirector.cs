using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Solitude.Audio;

/// <summary>
/// Real-time synthesized soundscape: one AudioStreamGenerator, mixed by hand in C#.
/// Beds: abyssal drone, ventilation hiss + electrical hum (inside), water flow over the
/// hull (speed), engine chug (rpm). Events: creaks and groans (more frequent with depth
/// stress and hard manoeuvres), distant calls, impacts, footsteps, console blips.
/// Routed through a "Hull" bus with a large metallic reverb.
///
/// Threading: synthesis runs on its own thread and keeps ~100 ms queued, so main-thread
/// hitches (chunk streaming, GC) can't starve the buffer. The game thread only writes
/// the float parameters below and enqueues voices.
/// </summary>
public partial class AudioDirector : Node
{
    public const float Rate = 22050f;
    private const float TargetLatency = 0.2f;
    private const int Block = 512;

    // Inputs (written by the game thread, read by the audio thread)
    public volatile float Depth;
    public volatile float Stress;     // 0..1.5 beyond test depth
    public volatile float EngineRpm;  // -1..1
    public volatile float Speed;      // m/s
    public volatile float TurnRate;   // deg/s
    public volatile bool Inside = true;
    public volatile float Hull = 100f;
    public volatile float Servo;      // manipulator hydraulics activity 0..1
    public volatile float Intake;     // intake filter running 0/1
    public volatile bool Diving;      // in the suit: breathing in the helmet, the world muffled
    public volatile float Exertion;   // 0..1, breathing rate
    public volatile float Heartbeat;  // 0..1, panic

    private AudioStreamGeneratorPlayback _pb;
    private Thread _thread;
    private volatile bool _running;
    private readonly ConcurrentQueue<Voice> _incoming = new();
    private readonly Random _mainRng = new();

    // ---- audio-thread state
    private readonly List<Voice> _voices = new();
    private readonly Random _rng = new();
    private readonly Vector2[] _buf = new Vector2[Block];
    private float _brown, _dronePh1, _dronePh2, _dronePh3, _humPh, _engPh, _chugPh, _t;
    private OnePole _rumbleHp = new(25f, Rate), _droneLp = new(110f, Rate),_hissLp = new(2500f, Rate), _hissHp = new(600f, Rate), _flowLp = new(700f, Rate), _engLp = new(400f, Rate);
    private float _servoPh;
    private float _breathPh, _bubbleT, _heartPh, _heartEnv, _bubblePh, _bubbleF, _bubbleEnv;
    private OnePole _muffleL = new(900f, Rate), _muffleR = new(900f, Rate), _breathLp = new(1400f, Rate), _exhaleLp = new(500f, Rate);
    private OnePole _servoLp = new(900f, Rate), _intakeLp = new(350f, Rate);
    private float _creakTimer = 6f, _groanTimer = 25f, _callTimer = 40f;
    private float _master;

    public override void _Ready()
    {
        int bus = AudioServer.BusCount;
        AudioServer.AddBus(bus);
        AudioServer.SetBusName(bus, "Hull");
        AudioServer.SetBusSend(bus, "Master");
        AudioServer.AddBusEffect(bus, new AudioEffectReverb
        {
            RoomSize = 0.75f,
            Damping = 0.55f,
            Spread = 0.8f,
            Wet = 0.28f,
            Dry = 0.85f,
            PredelayMsec = 25f,
        });

        var player = new AudioStreamPlayer
        {
            Stream = new AudioStreamGenerator { MixRate = Rate, BufferLength = 0.5f },
            Bus = "Hull",
            VolumeDb = -4f,
        };
        AddChild(player);
        player.Play();
        _pb = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();

        _running = true;
        _thread = new Thread(AudioLoop) { IsBackground = true, Name = "SolitudeAudio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>How many times the output ran dry (each one is an audible stutter).</summary>
    public long Skips => _pb?.GetSkips() ?? 0;

    // ------------------------------------------------------------ diagnostics
    // Records both what we synthesize (raw, pre-Godot) and what Godot actually outputs
    // (AudioEffectRecord on Master) so stutter can be analysed offline.
    private List<Vector2> _rawDump;
    private readonly object _dumpLock = new();
    private AudioEffectRecord _recorder;

    public void StartDebugRecording()
    {
        lock (_dumpLock) _rawDump = new List<Vector2>(22050 * 60);
        _recorder = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
        AudioServer.AddBusEffect(0, _recorder);
        _recorder.SetRecordingActive(true);
    }

    public void SaveDebugRecording(string dir)
    {
        if (_recorder != null)
        {
            _recorder.SetRecordingActive(false);
            _recorder.GetRecording()?.SaveToWav($"{dir}/audio_godot_output.wav");
        }
        Vector2[] raw;
        lock (_dumpLock) { raw = _rawDump?.ToArray(); _rawDump = null; }
        if (raw == null) return;
        var bytes = new byte[raw.Length * 4];
        for (int i = 0; i < raw.Length; i++)
        {
            short l = (short)Math.Clamp(raw[i].X * 32767f, -32768f, 32767f);
            short r = (short)Math.Clamp(raw[i].Y * 32767f, -32768f, 32767f);
            BitConverter.GetBytes(l).CopyTo(bytes, i * 4);
            BitConverter.GetBytes(r).CopyTo(bytes, i * 4 + 2);
        }
        var wav = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = (int)Rate, Stereo = true, Data = bytes };
        wav.SaveToWav($"{dir}/audio_raw_synth.wav");
    }

    public override void _ExitTree()
    {
        _running = false;
        _thread?.Join(200);
    }

    // ------------------------------------------------------------ event API (game thread)

    private void Add(Voice v) => _incoming.Enqueue(v);

    public void PlayImpact(float strength) => Add(new ImpactVoice(Rate, _mainRng, strength) { Pan = (float)(_mainRng.NextDouble() - 0.5) });
    public void PlayStep() => Add(new StepVoice(Rate, _mainRng));
    public void PlayBlip(float freq = 880f, float dur = 0.06f, float gain = 0.6f) => Add(new BlipVoice(Rate, _mainRng, freq, dur, gain));
    public void PlayCall() => Add(new CallVoice(Rate, _mainRng));
    /// <summary>One-shot effect (see SfxVoice) with stereo pan -1..1 and gain 0..1.</summary>
    public void PlayAt(string kind, float pan, float gain) { if (gain > 0.01f) Add(new SfxVoice(Rate, _mainRng, kind, pan, gain)); }
    public void PlayPing() => Add(new PingVoice(Rate, _mainRng, echo: false));
    public void PlayEcho(float delay, float strength) => Add(new DelayedVoice(Rate, _mainRng, new PingVoice(Rate, _mainRng, echo: true, strength), delay));

    // ------------------------------------------------------------ audio thread

    private void AudioLoop()
    {
        int capacity = _pb.GetFramesAvailable();      // empty buffer = full capacity
        int target = (int)(TargetLatency * Rate);
        while (_running)
        {
            try
            {
                // Top up in fixed blocks (no allocations) until ~TargetLatency is queued.
                int available = _pb.GetFramesAvailable();
                while (capacity - available < target && available >= Block)
                {
                    Render(Block);
                    available = _pb.GetFramesAvailable();
                }
            }
            catch (Exception e)
            {
                GD.PushError($"Audio thread: {e.Message}");
            }
            Thread.Sleep(2);
        }
    }

    private void Render(int frames)
    {
        while (_incoming.TryDequeue(out var v))
            if (_voices.Count < 24) _voices.Add(v);

        float inv = 1f / Rate;
        Schedule(frames * inv);


        float inside = Inside ? 1f : 0f;
        float rpm = MathF.Abs(EngineRpm);
        float flow = Math.Clamp(MathF.Abs(Speed) / 5f, 0f, 1f);

        for (int i = 0; i < frames; i++)
        {
            _t += inv;
            _master = MathF.Min(1f, _master + inv * 0.25f); // fade in at start

            // Abyssal drone: brown noise rumble + slowly beating low sines.
            // Gain staging matters here: brown noise wanders to large infrasonic offsets, so it is
            // high-passed (25 Hz) before the low-pass, and the whole bed sits around -18 dBFS RMS.
            // An earlier version ran it ~6x hotter and lived inside the tanh clipper = audible crackle.
            float w = (float)_rng.NextDouble() * 2f - 1f;
            _brown = (_brown + w * 0.02f) * 0.998f;
            float brownHp = _brown - _rumbleHp.Process(_brown);
            float rumble = _droneLp.Process(brownHp) * 1.6f;
            _dronePh1 = Wrap(_dronePh1 + 41f * inv);
            _dronePh2 = Wrap(_dronePh2 + 41.7f * inv);
            _dronePh3 = Wrap(_dronePh3 + 55f * inv);
            float swell = 0.6f + 0.4f * MathF.Sin(_t * 0.07f) * MathF.Sin(_t * 0.031f + 1f);
            float drone = (MathF.Sin(_dronePh1 * MathF.Tau) + MathF.Sin(_dronePh2 * MathF.Tau)) * 0.035f + MathF.Sin(_dronePh3 * MathF.Tau) * 0.03f * swell;

            // Inside: ventilation hiss + electrical hum.
            float hiss = (_hissLp.Process(w) - _hissHp.Process(w)) * 0.05f * inside;
            _humPh = Wrap(_humPh + 50f * inv);
            float hum = (MathF.Sin(_humPh * MathF.Tau) * 0.02f + MathF.Sin(_humPh * 2f * MathF.Tau) * 0.012f) * inside;

            // Water rushing past the hull.
            float rush = _flowLp.Process(w) * flow * 0.35f;

            // Engine: a smooth harmonic hum (fundamental kept above ~45 Hz: anything near 25-30 Hz
            // is heard as discrete pulses, i.e. flutter/stutter), with a gentle blade-rate swell.
            _engPh = Wrap(_engPh + (46f + rpm * 38f) * inv);
            _chugPh = Wrap(_chugPh + (0.8f + rpm * 4.5f) * inv);
            float a = _engPh * MathF.Tau;
            float tone = MathF.Sin(a) + 0.35f * MathF.Sin(2f * a) + 0.12f * MathF.Sin(3f * a);
            float chug = 1f - 0.22f * rpm * (0.5f + 0.5f * MathF.Sin(_chugPh * MathF.Tau));
            float eng = _engLp.Process(tone) * (0.012f + rpm * 0.1f) * chug;

            // Arm hydraulics: a strained, slightly wavering whine. Intake: pump drone + water hiss.
            _servoPh = Wrap(_servoPh + (170f + Servo * 140f + MathF.Sin(_t * 7f) * 6f) * inv);
            float servo = _servoLp.Process(_servoPh * 2f - 1f) * Servo * 0.07f;
            float intake = (_intakeLp.Process(w) * 0.08f + MathF.Sin(_humPh * 1.5f * MathF.Tau) * 0.015f) * Intake;

            float bed = rumble + drone + hiss + hum + rush + eng + servo + intake;
            float l = bed, r = bed;

            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                float s = voice.Next(out bool alive);
                if (!float.IsFinite(s)) { _voices.RemoveAt(v); continue; }
                l += s * (1f - MathF.Max(0f, voice.Pan));
                r += s * (1f + MathF.Min(0f, voice.Pan));
                if (!alive) _voices.RemoveAt(v);
            }

            // In the suit: the world arrives through the helmet (muffled), your breath does not.
            if (Diving)
            {
                l = _muffleL.Process(l) * 0.85f;
                r = _muffleR.Process(r) * 0.85f;
                float b = Breath(inv, w);
                l += b; r += b;
            }
            float hb = Heart(inv);
            l += hb; r += hb;

            // Soft limiter only: linear below ~0.6, tanh shoulder above (safety net for big impacts).
            _buf[i] = new Vector2(SoftLimit(l * _master), SoftLimit(r * _master));
        }
        _pb.PushBuffer(_buf);
        if (_rawDump != null)
            lock (_dumpLock) _rawDump?.AddRange(_buf);
    }

    private static float Wrap(float ph) => ph >= 1f ? ph - MathF.Floor(ph) : ph;

    /// <summary>Helmet breathing: inhale hiss, short pause, bubbling exhale. Faster with exertion.</summary>
    private float Breath(float inv, float w)
    {
        float period = 4.2f - Exertion * 2.3f - Heartbeat * 0.6f;
        _breathPh += inv / MathF.Max(1.2f, period);
        if (_breathPh >= 1f) _breathPh -= 1f;
        float s = 0f;
        if (_breathPh < 0.38f)
        {
            float u = _breathPh / 0.38f;
            s += _breathLp.Process(w) * MathF.Sin(MathF.PI * u) * (0.1f + Exertion * 0.08f);
        }
        else if (_breathPh > 0.48f && _breathPh < 0.95f)
        {
            float u = (_breathPh - 0.48f) / 0.47f;
            s += _exhaleLp.Process(w) * MathF.Sin(MathF.PI * u) * (0.16f + Exertion * 0.1f);
            // Bubbles leaving the regulator
            _bubbleT -= inv;
            if (_bubbleT <= 0f)
            {
                _bubbleT = 0.02f + (float)_rng.NextDouble() * 0.07f;
                _bubbleF = 250f + (float)_rng.NextDouble() * 700f;
                _bubbleEnv = 1f;
            }
        }
        if (_bubbleEnv > 0.001f)
        {
            _bubbleF *= 1f + inv * 3f;                    // pops chirp upward
            _bubblePh = Wrap(_bubblePh + _bubbleF * inv);
            s += MathF.Sin(_bubblePh * MathF.Tau) * _bubbleEnv * 0.05f;
            _bubbleEnv *= 1f - inv * 60f;
        }
        return s;
    }

    /// <summary>Heartbeat (lub-dub) when air runs low or something big is near.</summary>
    private float Heart(float inv)
    {
        float hb = Heartbeat;
        if (hb < 0.02f) { _heartEnv = 0f; return 0f; }
        float bpm = 70f + hb * 60f;
        float prev = _heartPh;
        _heartPh = Wrap(_heartPh + inv * bpm / 60f);
        if (_heartPh < prev || (prev < 0.28f && _heartPh >= 0.28f)) _heartEnv = 1f;
        _heartEnv *= 1f - inv * 14f;
        return MathF.Sin(_heartPh * MathF.Tau * 9f) * _heartEnv * 0.3f * hb;
    }

    private static float SoftLimit(float x)
    {
        const float knee = 0.6f;
        if (!float.IsFinite(x)) return 0f; // never let one bad voice poison the stream
        float ax = MathF.Abs(x);
        if (ax <= knee) return x;
        return MathF.Sign(x) * (knee + (1f - knee) * MathF.Tanh((ax - knee) / (1f - knee)));
    }

    /// <summary>Ambient event scheduling (audio thread).</summary>
    private void Schedule(float dt)
    {
        float strain = Math.Clamp(Stress + MathF.Abs(TurnRate) / 10f + (1f - Hull / 100f) * 0.5f, 0f, 2f);
        float depthFactor = Math.Clamp(Depth / 800f, 0.2f, 1.5f);

        _creakTimer -= dt * (0.6f + depthFactor + strain * 2f);
        if (_creakTimer <= 0f)
        {
            _voices.Add(new CreakVoice(Rate, _rng, 0.3f + strain * 0.4f) { Pan = (float)(_rng.NextDouble() * 2 - 1) * 0.7f });
            _creakTimer = 5f + (float)_rng.NextDouble() * 14f;
        }
        _groanTimer -= dt * (0.5f + depthFactor * 0.5f + strain);
        if (_groanTimer <= 0f)
        {
            _voices.Add(new GroanVoice(Rate, _rng, 0.3f + strain * 0.4f) { Pan = (float)(_rng.NextDouble() * 2 - 1) * 0.4f });
            _groanTimer = 18f + (float)_rng.NextDouble() * 35f;
        }
        _callTimer -= dt;
        if (_callTimer <= 0f)
        {
            _voices.Add(new CallVoice(Rate, _rng));
            _callTimer = 70f + (float)_rng.NextDouble() * 120f;
        }
    }
}
