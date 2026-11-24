#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glome;
using Glome.Core;
using Godot;

[Tool]
public partial class Acceptance : Node
{
    private const string Minimal = "res://addons/glome/Examples/Minimal.tscn";
    private const string Cel = "res://Cel.tscn";
    private static readonly Vector2I Hd = new(1920, 1080);
    private string Shots => OS.GetEnvironment("GLOME_SHOTS");
    private Camera3D _prior = null!;

    public override async void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        if (Engine.IsEditorHint() || !args.Contains("--accept")) return;
        try
        {
            _prior = new Camera3D();
            AddChild(_prior);
            _prior.MakeCurrent();
            GetWindow().Size = Hd;
            await Frames(6);
            if (args.Contains("--cold"))
            {
                Check(!OS.HasFeature("editor") && !OS.HasFeature("debug"), "Windows release template");
                foreach (var file in new[] { "abi", "camera", "intersections", "lighting" })
                    Check(!Godot.FileAccess.FileExists($"res://addons/glome/Shaders/glome_{file}.glslinc"), "raw include in PCK");
                if (args.Contains("--cel") && GetTree().CurrentScene.SceneFilePath != Cel)
                {
                    Check(GetTree().ChangeSceneToFile(Cel) == Error.Ok, "open packed cel scene");
                    await Frames(8);
                }
            }
            var space = (S3Space3D)GetTree().CurrentScene;
            Check(space.AutoStart && space.BakeRevision == 1 && space.LastBake!.Collision.ProxyCount >= 2, "automatic bake");
            Check(GetViewport().GetCamera3D() is { CullMask: 0 } native && native != _prior &&
                  native.GlobalPosition.IsZeroApprox(), "origin-aligned culling camera");
            Check(space.GetChildren(true).OfType<Camera3D>().Count() == 1, "one native camera");
            var texture = space.OutputTexture;
            Check(texture is Texture2Drd && texture.GetSize() == Hd, "native 1080p Vulkan output");
            if (args.Contains("--authored"))
                await CheckAuthored(space);
            else if (args.Contains("--cold") || args.Contains("--cel"))
            {
                bool cel = args.Contains("--cel");
                Check(space.SceneFilePath == (cel ? Cel : Minimal), "actual packed scene");
                var shader = cel ? space.RenderProgram!.Shader! :
                    GD.Load<RDShaderFile>("res://addons/glome/Shaders/glome_standard.glsl");
                using var spirv = shader.GetSpirV();
                Check(spirv.BytecodeCompute.Length > 0 && spirv.CompileErrorCompute == "", "imported compute stage");
                var reference = cel && args.Contains("--cold") ? "04-fresh-custom-cel-compute" : "01-fresh-minimal-f6";
                using var frame = Frame();
                using var expected = Image.LoadFromFile(Path.Combine(Shots, reference + ".png"));
                double change = Difference(expected, frame);
                if (args.Contains("--cold")) Check(change < 0.003, "cold frame matches fresh Vulkan rendering");
                else
                {
                    Check(change > 0.07, "cel shader visibly changes the scene");
                    Save(frame, "04-fresh-custom-cel-compute");
                }
                GD.Print($"Compute {shader.ResourcePath}, SPIR-V={spirv.BytecodeCompute.Length} bytes, RGB mean difference={change:F5}.");
            }
            else
            {
                Check(space.SceneFilePath == Minimal, "F6 launched Minimal directly");
                using var initial = Frame();
                Save(initial, "01-fresh-minimal-f6");
                await CheckPost(space, initial);
                await Walk(space);
            }

            space.QueueFree();
            await Frames(3);
            Check(GetViewport().GetCamera3D() == _prior && _prior.Current, "restored previous native camera");
            Check(Input.MouseMode != Input.MouseModeEnum.Captured, "released controller input");
            if (!args.Contains("--cold") && !args.Contains("--cel") && !args.Contains("--authored"))
                await CheckFloorless();
            GD.Print("[GLOME-RUNTIME-PASS] " + string.Join(" ", args));
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private Image Frame()
    {
        var image = GetViewport().GetTexture().GetImage();
        Check(image.GetSize() == Hd, "viewport screenshot dimensions");
        var values = Enumerable.Range(0, 144).Select(i => image.GetPixel((2*(i%16)+1)*Hd.X/32,
            (2*(i/16)+1)*Hd.Y/18).Luminance).ToArray();
        Check(values.Max() - values.Min() > 0.08 && values.Average() > 0.10, "non-black scene with spatial contrast");
        return image;
    }

    private void Save(Image frame, string name) =>
        Check(frame.SavePng(Path.Combine(Shots, name + ".png")) == Error.Ok, "save " + name);

    private static double Difference(Image a, Image b)
    {
        Check(a.GetSize() == b.GetSize(), "comparison size");
        a.Convert(Image.Format.Rgb8);
        b.Convert(Image.Format.Rgb8);
        byte[] first = a.GetData(), second = b.GetData();
        long sum = 0;
        for (int i = 0; i < first.Length; i++) sum += Math.Abs(first[i] - second[i]);
        return sum / (255.0 * first.Length);
    }

    private async Task CheckPost(S3Space3D space, Image initial)
    {
        var output = space.OutputTexture;
        space.OutputMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://grade.gdshader") };
        await Frames(4);
        using var post = Frame();
        double change = Difference(initial, post);
        Check(change > 0.06 && space.BakeRevision == 1 && space.OutputTexture == output, "canvas post-process");
        Save(post, "03-fresh-gdshader-post");
        GD.Print($"Layer 2 RGB mean difference={change:F5}, same compute texture and bake.");
        space.OutputMaterial = null;
        await Frames(3);
    }

    private async Task Walk(S3Space3D space)
    {
        var walker = space.GetNode<S3CharacterController>("Walker");
        var eye = walker.GetNode<S3Camera3D>("Eye");
        var foot = eye.EyeFrame(space.Radius).P.Reject(D4.Up).Unit();
        double ChartZ() { var p = eye.EyeFrame(space.Radius).P; return space.Radius * p.Z / p.W; }
        // Feed a physical W event to the engine, then give the normal physics controller time to reach the plinth.
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = true });
        for (int i = 0; i < 100; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = false });
        double z = ChartZ();
        var model = space.GetNode<S3Anchor3D>("Orrery");
        var shape = (CylinderShape3D)space.GetNode<CollisionShape3D>("Orrery/PlinthBody/Collision").Shape;
        Check(Math.Abs(z - (model.Position.Z + shape.Radius + walker.BodyRadius)) < 0.004, "walk to authored cylinder");
        walker.Move(Vector3.Forward, 0.5);
        Check(Math.Abs(z - ChartZ()) < 0.001, "no penetration on sustained input");
        double arc = foot.AngleTo(eye.EyeFrame(space.Radius).P.Reject(D4.Up).Unit()) * space.Radius;
        GD.Print($"Physical W: walked {arc:F4} m, contact at chart z={z:F4} m, forward retest {Math.Abs(z-ChartZ()):F6} m.");

        walker.Move(Vector3.Right, 0.58);
        walker.Look(new(-0.92f / walker.MouseSensitivity, 0));
        await Frames(4);
        using var walked = Frame();
        Save(walked, "02-fresh-after-walk");

        GetWindow().Size = new(1120, 720);
        await Frames(6);
        Check(space.OutputTexture!.GetSize() == new Vector2I(1120, 720), "resize rebuilt output image");
        using var resized = GetViewport().GetTexture().GetImage();
        Check(resized.GetSize() == new Vector2I(1120, 720) && resized.GetPixel(560,360).Luminance > 0.04, "resized dispatch");
        GetWindow().Size = Hd;
        await Frames(5);
        Check(space.OutputTexture.GetSize() == Hd, "restore output resolution");
    }

    // The editor calls this against its last presented, owner-bearing bake. Runtime compares the
    // resulting ray and camera with its independent automatic bake of the same authored scene.
    public double[] Measure(S3Space3D space)
    {
        var view = space.GetNode<S3Camera3D>("Walker/Eye").EyeFrame(space.Radius);
        var bake = space.LastBake!;
        var hit = bake.Geometry.Trace(new(view.P, -view.Back));
        Check(hit.Kind == HitKind.Triangle, "center ray reaches authored box");
        if (Engine.IsEditorHint())
            Check(bake.TriangleOwners![hit.Triangle] == space.GetNode("Target"), "presented pick owner");
        return [view.P.X, view.P.Y, view.P.Z, view.P.W, hit.T, hit.Uv.X, hit.Uv.Y];
    }

    public Vector4 VertexMean(S3Space3D space, Node3D owner)
    {
        var bake = space.LastBake!;
        Check(bake.Geometry.Radius == space.Radius, "presented bake has current radius");
        var sum = default(D4);
        int count = 0;
        for (int i = 0; i < bake.Geometry.Triangles.Length; i++)
        {
            if (bake.TriangleOwners![i] != owner) continue;
            var tri = bake.Geometry.Triangles[i];
            sum += tri.A.V + tri.B.V + tri.C.V;
            count += 3;
        }
        Check(count > 0, "baked faces owned by " + owner.Name);
        return GlomeBuffers.Vec(sum / count);
    }

    private async Task CheckAuthored(S3Space3D space)
    {
        var before = Measure(space);
        var saved = new ConfigFile();
        Check(saved.Load(OS.GetEnvironment("GLOME_EDITOR_REFERENCE")) == Error.Ok, "editor measurement");
        var reference = saved.GetValue("frame", "sample").AsFloat64Array();
        double error = before.Zip(reference, (a,b) => Math.Abs(a-b)).Max();
        Check(before.Length == reference.Length && error < 1e-10, "runtime/editor ray, pose and UV agreement");
        var camera = space.GetNode<S3Camera3D>("Walker/Eye");
        var walker = space.GetNode<S3CharacterController>("Walker");
        walker.Move(Vector3.Forward, 2);
        double travelled = D4.Pole.AngleTo(camera.EyeFrame(space.Radius).P.Reject(D4.Up).Unit()) * space.Radius;
        double expected = 4 - space.Radius * Math.Atan((0.6 + walker.BodyRadius)/space.Radius);
        Check(Math.Abs(travelled - expected) < 0.002, "box contact at authored arc placement");
        await Frames(3);
        GD.Print($"Authored agreement: max delta {error:E2}, box contact {travelled:F4} m (expected {expected:F4} m).");
    }

    private async Task CheckFloorless()
    {
        var space = GD.Load<PackedScene>(Minimal).Instantiate<S3Space3D>();
        space.Environment = new S3Environment { EquatorialGroundEnabled = false };
        var custom = new TextureRect { Size = new(480, 320), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
        AddChild(custom);
        space.OutputTarget = custom;
        space.ResolutionScale = 0.5f;
        space.DisplayFilter = CanvasItem.TextureFilterEnum.Nearest;
        AddChild(space);
        await Frames(5);
        var walker = space.GetNode<S3CharacterController>("Walker");
        Check(walker.Flying && !space.LastBake!.Collision.EquatorialGroundEnabled, "automatic free flight");
        Check(custom.Texture == space.OutputTexture && custom.Texture.GetSize() == new Vector2I(240,160) &&
              custom.TextureFilter == CanvasItem.TextureFilterEnum.Nearest, "custom output size and filter");
        walker.Move(Vector3.Up, 0.5);
        Check(walker.GetNode<S3Camera3D>("Eye").EyeFrame(space.Radius).P.Y > 0.15, "vertical flight");
        space.QueueFree();
        await Frames(3);
        Check(_prior.Current && custom.Texture == null && custom.TextureFilter == CanvasItem.TextureFilterEnum.ParentNode,
            "released external target and camera");
        custom.QueueFree();
    }

    private static void Check(bool ok, string detail)
    {
        if (!ok) throw new InvalidOperationException(detail);
    }
}
