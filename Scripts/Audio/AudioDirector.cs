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
    public volatile float Exertion;
    public volatile float SwimMotion; // 0..1, the diver moving or turning: the bubbles come from this   // 0..1, breathing rate
    public volatile float Heartbeat;  // 0..1, panic
    public volatile float Unease;     // 0..1, story progress: the distant calls come more often
    public volatile float Silt;       // 0..1, a silt storm outside: a roar, and grit ticking on the hull
    public volatile float Spray;      // 0..1, open hull breaches: water forcing in
    // Hydrophone headphones: when on, the boat is muffled and the beam's sound is synthesised.
    public volatile bool Hydro;
    /// <summary>Hiding in a locker: your own breathing, loud (silent while you hold it).</summary>
    public volatile bool Hiding, BreathHeld;
    public volatile float HydLow, HydMid, HydHigh, HydPulseHz, HydDuty = 1f, HydToneHz, HydTone, HydSelf;
    private float _hydOn, _hydPulsePh, _hydTonePh, _hLo, _hMi, _hHi;
    private readonly BandPass _hydLowBp = new(140f, 1.2f, Rate), _hydMidBp = new(900f, 1.6f, Rate), _hydHighBp = new(3600f, 2.2f, Rate);
    private readonly OnePole _hydSelfLp = new(220f, Rate);

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
    private readonly BubbleBank _bubbles = new(Rate);
    public int BubbleCount => _bubbles.Count;
    /// <summary>The helmet's breathing hiss (Tom: "a little bit too loud", then "barely audible"): 0.3 ≈ 10 dB down.</summary>
    private const float BreathLevel = 0.3f;   // (Tom, again: "barely audible"; the heart carries it now)
    private bool _exhaling;
    private float _breathPh, _bubbleT, _heartPh, _heartEnv, _bubblePh, _bubbleF, _bubbleEnv;
    private OnePole _muffleL = new(900f, Rate), _muffleR = new(900f, Rate), _breathLp = new(1400f, Rate), _exhaleLp = new(500f, Rate);
    private OnePole _servoLp = new(900f, Rate), _intakeLp = new(350f, Rate);
    private OnePole _sprayLp = new(2500f, Rate), _sprayLowLp = new(300f, Rate);
    private OnePole _siltLp = new(160f, Rate), _siltLp2 = new(900f, Rate);
    private readonly BandPass _gritBp = new(3400f, 5f, Rate);
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
        _player = player;
        _baseDb = player.VolumeDb;

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
    private AudioStreamPlayer _player;
    private float _baseDb;

    /// <summary>Lower (negative dB) or restore (0) the whole synthesized soundscape, smoothly.</summary>
    public void Duck(float db, float seconds) => CreateTween().TweenProperty(_player, "volume_db", _baseDb + db, seconds);

    public void PlayCall() => Add(new CallVoice(Rate, _mainRng));
    public void PlayGroan(float intensity) => Add(new GroanVoice(Rate, _mainRng, intensity));
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
            // Breach spray: a hard, bright hiss with a low roar under it, wobbling with pressure.
            float spr = Spray;
            if (spr > 0.001f)
            {
                float hissS = (w - _sprayLp.Process(w)) * 0.22f;
                float roar = _sprayLowLp.Process(w) * 0.5f;
                intake += (hissS + roar) * spr * (0.85f + 0.15f * MathF.Sin(_t * 7.3f));
            }

            // Silt storm: a deep surging roar (the whole water column moving) and fine grit
            // ticking on the hull in random bursts, like sand thrown at a window.
            float sl = Silt;
            if (sl > 0.001f)
            {
                float surge = 0.7f + 0.3f * MathF.Sin(_t * 0.9f) * MathF.Sin(_t * 0.37f);
                float roarS = _siltLp.Process(w) * 1.1f + (_siltLp2.Process(w) - _siltLp.Process(w) * 0.5f) * 0.12f;
                // (the mixer thread's own white noise decides the ticks: no shared Random across threads)
                float gate = w * 0.5f + 0.5f < 0.004f * sl * surge ? 1f : 0f;
                float grit = _gritBp.Process(gate + w * 0.01f) * 0.6f;
                intake += (roarS * surge * 0.5f + grit) * sl * (Inside ? 0.8f : 1f);
            }

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

            // Hydrophone headphones: the boat fades back, the sea comes forward.
            _hydOn += ((Hydro ? 1f : 0f) - _hydOn) * inv * 4f;
            if (_hydOn > 0.001f)
            {
                l *= 1f - 0.88f * _hydOn; r *= 1f - 0.88f * _hydOn;   // the boat drops well back: its hum sits right where the Sounding is
                _hLo += (HydLow - _hLo) * inv * 3f; _hMi += (HydMid - _hMi) * inv * 3f; _hHi += (HydHigh - _hHi) * inv * 3f;
                float env = 1f;
                float ph = HydPulseHz;
                if (ph > 0f)
                {
                    _hydPulsePh = Wrap(_hydPulsePh + ph * inv);
                    float duty = MathF.Max(0.05f, HydDuty);
                    env = _hydPulsePh < duty ? MathF.Sin(MathF.PI * _hydPulsePh / duty) : 0f;
                    env = 0.15f + 0.85f * env;
                }
                _hydTonePh = Wrap(_hydTonePh + MathF.Max(1f, HydToneHz) * inv);
                // Band-passed noise keeps only a sliver of the noise's energy: hence the big gains.
                float sea = (_hydLowBp.Process(w) * _hLo * 1.35f + _hydMidBp.Process(w) * _hMi * 1.05f + _hydHighBp.Process(w) * _hHi * 0.75f) * 3.2f;
                // Harmonics, so a 50-73 Hz hum is still heard on headphones and laptop speakers.
                float tp = _hydTonePh * MathF.Tau;
                float hTone = (MathF.Sin(tp) + 0.6f * MathF.Sin(2f * tp) + 0.35f * MathF.Sin(3f * tp)) * HydTone * 0.55f;
                float self = _hydSelfLp.Process(w) * HydSelf * 0.9f;
                // Soft limit: a ravager in the beam is loud, but it shouldn't crackle.
                float hyd = MathF.Tanh(((sea + hTone) * env + self + w * 0.004f) * 1.3f) * 0.6f * _hydOn;
                l += hyd; r += hyd;
            }

            // In the suit: the world arrives through the helmet (muffled), your breath does not.
            if (Diving)
            {
                l = _muffleL.Process(l) * 0.85f;
                r = _muffleR.Process(r) * 0.85f;
                float b = Breath(inv, w);
                l += b; r += b;
                var (bl, br) = _bubbles.Next(inv);
                l += bl; r += br;
            }
            if (Hiding && !Diving && !BreathHeld)
            {
                float hbr = Breath(inv, w) * 1.3f;
                l += hbr; r += hbr;
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
            s += _breathLp.Process(w) * MathF.Sin(MathF.PI * u) * (0.1f + Exertion * 0.08f) * BreathLevel;
        }
        else if (_breathPh > 0.48f && _breathPh < 0.95f)
        {
            float u = (_breathPh - 0.48f) / 0.47f;
            s += _exhaleLp.Process(w) * MathF.Sin(MathF.PI * u) * (0.16f + Exertion * 0.1f) * BreathLevel;
            // The regulator lets go: a gush of air that bubbles out past the helmet (bulgotanie).
            if (!_exhaling) { _exhaling = true; if (SwimMotion > 0.08f) _bubbles.Exhale(Exertion, SwimMotion); }
        }
        else _exhaling = false;
        // While you swim or turn, water tumbles bubbles past the visor: more, the faster you move.
        _bubbleT -= inv;
        if (_bubbleT <= 0f)
        {
            float m = SwimMotion;
            _bubbleT = m > 0.08f ? (0.04f + (float)_rng.NextDouble() * 0.22f) / m : 0.25f;
            if (m > 0.08f) _bubbles.Stray();
        }
        return s;
    }

    /// <summary>Heartbeat (lub-dub) when air runs low or something big is near.</summary>
    /// <summary>
    /// Your heart, heard from inside: lub-dub, a soft low thump (~55 Hz falling, with a little overtone so
    /// laptop speakers carry it too). Heartbeat 0..1: in the suit it rests around 0.2 (barely there, ~80
    /// bpm), and climbs with effort, low air and anything big nearby (louder, up to ~130 bpm).
    /// </summary>
    private float Heart(float inv)
    {
        // No hard cut-offs (each one is a click, Tom heard them): the overall level glides, each beat is
        // its own voice starting at zero phase with a short fade-in, and lub and dub ring on separately so
        // the second never chops off the first's tail.
        float hb = Heartbeat;
        float target = hb < 0.02f ? 0f : 0.05f + 0.3f * hb;
        _heartGain += (target - _heartGain) * MathF.Min(1f, inv * 4f);
        if (_heartGain < 0.0005f && target == 0f) { _hv0.Env = _hv1.Env = 0f; return 0f; }
        float bpm = 68f + MathF.Max(hb, 0.02f) * 62f;
        float prev = _heartPh;
        _heartPh = Wrap(_heartPh + inv * bpm / 60f);
        if (hb >= 0.02f)
        {
            if (_heartPh < prev) _hv0.Start(58f, 1f);                       // lub
            else if (prev < 0.3f && _heartPh >= 0.3f) _hv1.Start(50f, 0.65f); // dub
        }
        return (_hv0.Next(inv) + _hv1.Next(inv)) * _heartGain;
    }

    private float _heartGain;
    private HeartVoice _hv0 = new(), _hv1 = new();

    private sealed class HeartVoice
    {
        public float Env;
        private float _f, _amp, _ph, _att;
        public void Start(float f, float amp) { _f = f; _amp = amp; _ph = 0f; _att = 0f; Env = 1f; }
        public float Next(float inv)
        {
            if (Env < 0.0005f) return 0f;
            _f *= 1f - inv * 2.2f;                                   // each thump sags in pitch
            _ph += _f * inv; _ph -= MathF.Floor(_ph);
            _att = MathF.Min(1f, _att + inv / 0.008f);              // 8 ms fade-in
            float body = MathF.Sin(_ph * MathF.Tau) + 0.35f * MathF.Sin(_ph * 2f * MathF.Tau);
            float v = body * Env * _att * _amp;
            Env *= 1f - inv * 16f;
            return v;
        }
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
            _callTimer = (70f + (float)_rng.NextDouble() * 120f) * (1f - 0.6f * Unease);
        }
    }
}
