using Godot;
using Solitude.Core;

namespace Solitude.World;

/// <summary>
/// Owns the underwater look: the world Environment (exponential + volumetric fog, glow),
/// depth-driven darkening, and the marine snow that drifts around the submarine.
/// Also provides the interior Environment used by the player's camera inside the sub.
/// </summary>
public partial class Ocean : Node3D
{
    public Environment Water { get; private set; }
    public Environment Interior { get; private set; }
    public System.Func<Vector3> Focus;

    private GpuParticles3D _snow;
    private GpuParticles3D _motes;

    public override void _Ready()
    {
        Water = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = new Color(0.005f, 0.02f, 0.025f),
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.08f, 0.16f, 0.18f),
            AmbientLightEnergy = 0.35f,
            TonemapMode = Environment.ToneMapper.Filmic,
            TonemapExposure = 1.1f,

            FogEnabled = true,
            FogMode = Environment.FogModeEnum.Exponential,
            FogLightColor = new Color(0.01f, 0.04f, 0.05f),
            FogDensity = 0.022f,
            FogSkyAffect = 1f,

            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.016f,
            VolumetricFogAlbedo = new Color(0.55f, 0.75f, 0.78f),
            VolumetricFogEmission = new Color(0.0f, 0.004f, 0.006f),
            VolumetricFogLength = 90f,
            VolumetricFogDetailSpread = 1.5f,
            VolumetricFogAnisotropy = 0.55f,
            VolumetricFogTemporalReprojectionEnabled = true,

            GlowEnabled = true,
            GlowIntensity = 0.9f,
            GlowBloom = 0.08f,
            GlowHdrThreshold = 0.9f,
            GlowBlendMode = Environment.GlowBlendModeEnum.Additive,
        };
        AddChild(new WorldEnvironment { Environment = Water });

        Interior = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.18f, 0.2f, 0.22f),
            AmbientLightEnergy = 0.25f,
            TonemapMode = Environment.ToneMapper.Filmic,
            TonemapExposure = 1.05f,
            SsaoEnabled = true,
            SsaoRadius = 0.8f,
            SsaoIntensity = 1.6f,
            GlowEnabled = true,
            GlowIntensity = 0.7f,
            GlowBloom = 0.05f,
            GlowHdrThreshold = 1.0f,
            FogEnabled = true,
            FogMode = Environment.FogModeEnum.Depth,
            FogLightColor = new Color(0.02f, 0.025f, 0.03f),
            FogDepthBegin = 6f,
            FogDepthEnd = 30f,
            FogDensity = 0.6f,
        };

        // Dense near-field marine snow plus a sparser layer of bigger clumps.
        _snow = MakeSnow(6000, 0.05f, new Color(0.85f, 0.88f, 0.8f, 0.85f), 28f);
        _motes = MakeSnow(700, 0.14f, new Color(0.7f, 0.75f, 0.65f, 0.6f), 30f);
        AddChild(_snow);
        AddChild(_motes);
    }

    private static GpuParticles3D MakeSnow(int amount, float size, Color color, float box)
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(box, box * 0.6f, box),
            Direction = new Vector3(0.2f, -1f, 0.1f),
            Spread = 40f,
            InitialVelocityMin = 0.02f,
            InitialVelocityMax = 0.12f,
            Gravity = new Vector3(0.02f, -0.035f, 0.01f),
            ScaleMin = 0.4f,
            ScaleMax = 1.3f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.25f,
            TurbulenceNoiseScale = 6f,
            TurbulenceNoiseSpeed = new Vector3(0.05f, 0.02f, 0.04f),
            DampingMin = 0.2f,
            DampingMax = 0.4f,
        };
        var mat = new StandardMaterial3D
        {
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            AlbedoColor = color,
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.5f,
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.07f, 0.07f),
            Roughness = 1f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        return new GpuParticles3D
        {
            Amount = amount,
            Lifetime = 26f,
            Preprocess = 26f,
            LocalCoords = false,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(size, size), Material = mat },
            VisibilityAabb = new Aabb(new Vector3(-box * 1.5f, -box, -box * 1.5f), new Vector3(box * 3f, box * 2f, box * 3f)),
            FixedFps = 30,
        };
    }

    public override void _Process(double delta)
    {
        if (Focus == null) return;
        Vector3 f = Focus();
        _snow.GlobalPosition = f;
        _motes.GlobalPosition = f;

        // The deeper you go the less ambient light leaks down and the thicker the dark.
        float dt = (float)delta;
        float depth = Mathf.Max(0f, -f.Y);
        float t = Mathf.Clamp((depth - 200f) / 800f, 0f, 1f);

        // Biome tint, eased so crossing a border feels like the water itself changing.
        float crystalNow = Gen != null ? Gen.CrystalField(f.X, f.Z) : 0f;
        float hadalNow = TerrainGenerator.HadalFactor(f.Y);
        _crystalT = Mathf.Lerp(_crystalT, crystalNow, Mathf.Clamp(0.4f * dt, 0f, 1f));
        _hadalT = Mathf.Lerp(_hadalT, hadalNow, Mathf.Clamp(0.4f * dt, 0f, 1f));

        Water.AmbientLightEnergy = Mathf.Lerp(0.9f, 0.35f, t) * (1f - 0.5f * _hadalT);
        Color fog = new Color(0.012f, 0.045f, 0.055f).Lerp(new Color(0.002f, 0.008f, 0.014f), t);
        fog = fog.Lerp(new Color(0.018f, 0.012f, 0.05f), _crystalT * 0.8f);       // violet glow in the Expanse
        fog = fog.Lerp(new Color(0.01f, 0.002f, 0.003f), _hadalT);                // red-black in the Hadal Deep
        Water.FogLightColor = fog;
        Water.BackgroundColor = fog;
        Water.FogDensity = Mathf.Lerp(0.011f, 0.02f, t) * (1f + 0.25f * _hadalT);
        Water.VolumetricFogAlbedo = new Color(0.55f, 0.75f, 0.78f).Lerp(new Color(0.68f, 0.55f, 0.95f), _crystalT).Lerp(new Color(0.65f, 0.32f, 0.28f), _hadalT);
        Water.AmbientLightColor = new Color(0.08f, 0.16f, 0.18f).Lerp(new Color(0.12f, 0.09f, 0.2f), _crystalT).Lerp(new Color(0.1f, 0.03f, 0.03f), _hadalT);
    }

    public TerrainGenerator Gen;
    private float _crystalT, _hadalT;
}
