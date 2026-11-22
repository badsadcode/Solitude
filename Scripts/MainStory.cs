using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.Stations;
using Solitude.World;

namespace Solitude;

/// <summary>
/// The story spine (milestone 6): every station's core holds a fragment of the Sounding, the
/// signal the crews kept hearing. Three fragments triangulate its source — a shaft in the Hadal
/// Deep. At the bottom waits the Choir. Reaching it ends the dive (and you may keep diving).
/// </summary>
public partial class Main
{
    private readonly HashSet<long> _coreSites = new();
    private bool _originKnown, _ended, _endingRunning;
    private Node3D _choir;
    private OmniLight3D _choirLight;
    private MeshInstance3D _choirHeart;
    private float _choirT;

    public const int CoresNeeded = 3;
    private Vector3 ChoirHeart => new(_gen.Origin.X, _gen.OriginFloor + 26f, _gen.Origin.Z);

    private static readonly string[] Fragments =
    {
        "DATA CORE 1/3. Under the noise floor: a slow pulse. Not sonar, not machinery. Something counting. The crews called it the Sounding.",
        "DATA CORE 2/3. The pulse has a direction, and the direction is down. Every station logged it. Every station stopped logging it.",
        "DATA CORE 3/3. Triangulated. The Sounding rises from a shaft in the Hadal Deep. The holo table has marked it. Your hull will not survive it as it is.",
    };

    private void SetupStory()
    {
        BuildChoir();
        // The holo table also shows the Origin once it's known.
        var stationBeacons = _interior.Holo.Beacons;
        _interior.Holo.Beacons = () =>
        {
            var list = new List<Vector3>(stationBeacons?.Invoke() ?? new List<Vector3>());
            if (_originKnown) list.Add(_gen.Origin);
            return list;
        };
    }

    /// <summary>Called when a station core's memory is read (alongside its map fragment).</summary>
    private void OnDataCore(StationSite site)
    {
        if (!_coreSites.Add(site.Id)) return;
        int n = Mathf.Min(_coreSites.Count, CoresNeeded);
        _audio.Unease = n / (float)CoresNeeded;
        _audio.PlayCall();
        if (_coreSites.Count >= CoresNeeded && !_originKnown)
        {
            _originKnown = true;
            float depth = -_gen.OriginFloor;
            _hud.Message($"{Fragments[2]} Bottom of the shaft: ~{depth:0} m.", 10f);
        }
        else _hud.Message(Fragments[Mathf.Clamp(n - 1, 0, 2)], 9f);
        Autosave("data core");
    }

    private string StoryHelmText()
    {
        if (!_originKnown || _ended) return "";
        Vector3 d = _gen.Origin - _sub.GlobalPosition;
        float brg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(d.X, -d.Z)), 360f);
        float dist = new Vector2(d.X, d.Z).Length();
        return $"\nSIGNAL   THE SOUNDING   BRG {Mathf.RoundToInt(brg) % 360:000}°   {(dist > 1000f ? $"{dist / 1000f:0.0} km" : $"{dist:0} m")}   SHAFT {-_gen.OriginFloor:0} m";
    }

    // ------------------------------------------------------------------ the Choir

    /// <summary>
    /// At the bottom of the shaft: nine colossal ribs curving up and inward over a slowly
    /// pulsing heart of light. Built once; it's only ever seen by those who come down.
    /// </summary>
    private void BuildChoir()
    {
        _choir = new Node3D { Name = "TheChoir" };
        _game.AddChild(_choir);
        _choir.GlobalPosition = new Vector3(_gen.Origin.X, _gen.OriginFloor, _gen.Origin.Z);

        var mb = new MeshBuilder();
        var bone = new Color(0.78f, 0.74f, 0.66f);
        for (int i = 0; i < 9; i++)
        {
            float a = Mathf.Tau * i / 9f;
            Vector3 outDir = new(Mathf.Sin(a), 0, Mathf.Cos(a));
            Vector3 prev = outDir * 44f;
            for (int k = 1; k <= 10; k++)
            {
                float t = k / 10f;
                // Rise and curl inward like the ribs of something kneeling over the light.
                Vector3 next = outDir * (44f - 30f * t * t) + Vector3.Up * (62f * Mathf.Sin(t * Mathf.Pi * 0.55f));
                Vector3 dir = next - prev;
                float th = 2.4f * (1f - t * 0.75f);
                mb.BoxOriented(new Transform3D(Basis.LookingAt(dir.Normalized(), Vector3.Up), (prev + next) * 0.5f), new Vector3(th, th, dir.Length()), bone.Shade(0.85f + t * 0.15f));
                prev = next;
            }
            mb.Rock(outDir * 44f, new Vector3(4f, 3f, 4f), bone.Shade(0.7f), 50 + i, 0.3f);
        }
        _choir.AddChild(new MeshInstance3D { Mesh = mb.Build(Art.Bone) });

        var heart = new MeshBuilder();
        heart.Rock(Vector3.Zero, new Vector3(6f, 7f, 6f), new Color(0.8f, 0.95f, 1f, 0.3f), 7, 0.35f);
        heart.Rock(Vector3.Zero, new Vector3(3.5f, 4f, 3.5f), new Color(1f, 1f, 1f, 0.2f), 8, 0.2f);
        var heartMat = (ShaderMaterial)Art.Crystal.Duplicate();
        heartMat.SetShaderParameter("emission_color", new Color(0.75f, 0.95f, 1f));
        heartMat.SetShaderParameter("emission_energy", 7f);
        _choirHeart = new MeshInstance3D { Mesh = heart.Build(heartMat), Position = Vector3.Up * 26f };
        _choir.AddChild(_choirHeart);
        _choirLight = new OmniLight3D { Position = Vector3.Up * 26f, LightColor = new Color(0.7f, 0.9f, 1f), OmniRange = 140f, OmniAttenuation = 0.8f, LightEnergy = 5f, LightVolumetricFogEnergy = 4f };
        _choir.AddChild(_choirLight);
    }

    private void UpdateStory(float dt)
    {
        _choirT += dt;
        if (_choirLight != null)
        {
            // A slow heartbeat of light you can see from the rim if you look down.
            float beat = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(_choirT * 0.9f)), 8f);
            _choirLight.LightEnergy = 4.5f + beat * 9f;
            _choirHeart.Rotation = new Vector3(0, _choirT * 0.05f, 0);
        }
        if (_mode == Mode.Title || _ended || _endingRunning) return;
        if (_originKnown && _sub.GlobalPosition.DistanceTo(ChoirHeart) < 55f) TriggerEnding();
    }

    // ------------------------------------------------------------------ the ending

    private void TriggerEnding()
    {
        _endingRunning = true;
        _sub.TelegraphIndex = 2; _sub.ApplyTelegraph();
        _sub.LinearVelocity = Vector3.Zero; _sub.AngularVelocity = Vector3.Zero;
        _sub.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        _sub.Freeze = true;
        _player.InputEnabled = false;
        _audio.Unease = 1f;
        for (int i = 0; i < 3; i++) _audio.PlayCall();
        _hud.Message("The light turns toward you.", 6f);
        var tw = CreateTween();
        tw.TweenInterval(5.0);
        tw.TweenCallback(Callable.From(() => _hud.FadeTo(1f, 4f)));
        tw.TweenInterval(4.5);
        tw.TweenCallback(Callable.From(ShowEpilogue));
    }

    private void ShowEpilogue()
    {
        _ended = true;
        _mode = Mode.Reading;
        _hud.SetGameplayHud(false);
        _hud.ShowHelm(false);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        string body =
            "At the bottom of the shaft there is no monster.\n\n" +
            "There is a voice, older than the stations and the crews and the ocean's name, and it has been " +
            "counting. Every lonely thing that sinks, it counts. The crews heard it counting them, and they " +
            "were afraid, because they thought it meant they were alone.\n\n" +
            "It counts you. One.\n\n" +
            "For the first time in a long time, you are not.\n\n\n" +
            "S O L I T U D E\n" +
            "a Bad Ideas Works game  ·  creatures by Tom  ·  engineering by Sage  ·  stations by NoorLevel";
        _hud.ShowPanel("THE SOUNDING", body, new List<(string, bool)> { ("Keep diving", true), ("Return to the title", true) }, i =>
        {
            _hud.HidePanel();
            if (i == 0)
            {
                _endingRunning = false;
                _sub.Freeze = false;
                _hud.SetGameplayHud(true);
                _hud.FadeTo(0f, 3f);
                EnterHelm();
                _hud.Message("The Sounding has stopped. The ocean is very quiet now.", 6f);
                Autosave("ending");
            }
            else
            {
                Autosave("ending");
                GetTree().ReloadCurrentScene();
            }
        });
        Autosave("ending");
    }
}
