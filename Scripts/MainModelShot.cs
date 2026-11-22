using System.Collections.Generic;
using Godot;

namespace Solitude;

/// <summary>
/// Dev tool for Tom's models: `-- --model-shot=res://assets/X.glb --model-out=<dir>` renders the model
/// three ways side by side, under neutral light, from four angles:
///   left   — its own imported material (what Blender exported)
///   middle — the game's creature shader (what you see in Solitude)
///   right  — surface normals as colours (smooth vs faceted, flipped patches show up as odd colours)
/// then quits. Writes <dir>/model_<angle>.png.
/// </summary>
public partial class Main
{
    private void ModelShot(string path, string outDir)
    {
        // Check the model loads before touching the world: a failed load used to leave two sun lamps
        // shining into the title screen.
        if (!ResourceLoader.Exists(path) || GD.Load<PackedScene>(path) == null)
        {
            GD.PrintErr($"[model-shot] can't load {path} (not imported yet?)");
            GetTree().Quit(1);
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(outDir);
        var stage = new Node3D { Name = "ModelStage", Position = new Vector3(0, 4000, 0) };
        _game.AddChild(stage);
        stage.AddChild(new DirectionalLight3D { Rotation = new Vector3(-0.8f, 0.6f, 0), LightEnergy = 1.4f });
        stage.AddChild(new DirectionalLight3D { Rotation = new Vector3(-0.3f, -2.4f, 0), LightEnergy = 0.5f, LightColor = new Color(0.7f, 0.8f, 1f) });
        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.19f, 0.2f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.35f, 0.35f, 0.38f) };
        var cam = new Camera3D { Fov = 40f, Environment = env };
        stage.AddChild(cam);

        var normalMat = new ShaderMaterial { Shader = new Shader { Code = "shader_type spatial; render_mode unshaded, cull_disabled; void fragment() { ALBEDO = (FRONT_FACING ? NORMAL : -NORMAL) * 0.5 + 0.5; }" } };
        var gameMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/creature.gdshader") };
        var models = new List<Node3D>();
        float size = 1f;
        for (int k = 0; k < 3; k++)
        {
            var m = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            stage.AddChild(m);
            models.Add(m);
            foreach (var mi in FindMeshes(m))
            {
                var aabb = mi.GetAabb();
                size = Mathf.Max(size * 0f + aabb.Size.Length(), 0.1f);
                if (k == 1)
                {
                    if (mi.Mesh?.SurfaceGetMaterial(0) is BaseMaterial3D src)
                    {
                        gameMat.SetShaderParameter("albedo_tex", src.AlbedoTexture);
                        gameMat.SetShaderParameter("mask_tex", src.AlbedoTexture);
                        gameMat.SetShaderParameter("eye_energy", 0f);
                        if (src.EmissionTexture != null) { gameMat.SetShaderParameter("use_emission_tex", true); gameMat.SetShaderParameter("emission_tex", src.EmissionTexture); gameMat.SetShaderParameter("emission_tex_energy", 4f); }
                    }
                    mi.MaterialOverride = gameMat;
                }
                if (k == 2) mi.MaterialOverride = normalMat;
            }
        }
        for (int k = 0; k < 3; k++) models[k].Position = new Vector3((k - 1) * size * 1.1f, 0, 0);
        cam.MakeCurrent();

        string[] names = { "front", "side", "back", "top" };
        Vector3[] dirs = { new(0, 0.35f, 1), new(1, 0.35f, 0.01f), new(0, 0.35f, -1), new(0.01f, 1, 0.2f) };
        int shot = 0;
        var timer = new Timer { WaitTime = 0.5, Autostart = true, ProcessCallback = Timer.TimerProcessCallback.Idle };
        AddChild(timer);
        void Place(int i) { cam.Position = dirs[i].Normalized() * size * 2.6f; cam.LookAt(stage.GlobalPosition, Mathf.Abs(dirs[i].Y) > 0.9f ? Vector3.Forward : Vector3.Up); }
        Place(0);
        timer.Timeout += () =>
        {
            GetViewport().GetTexture().GetImage().SavePng($"{outDir}/model_{names[shot]}.png");
            if (++shot >= names.Length) { GD.Print($"[model-shot] wrote {names.Length} views to {outDir}"); GetTree().Quit(); return; }
            Place(shot);
        };
    }

    private static IEnumerable<MeshInstance3D> FindMeshes(Node n)
    {
        if (n is MeshInstance3D mi) yield return mi;
        foreach (var c in n.GetChildren()) foreach (var r in FindMeshes(c)) yield return r;
    }
}
