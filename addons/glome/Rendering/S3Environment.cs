#nullable enable
using Godot;

namespace Glome;

public enum S3ShadingMode { StandardLit, Unlit, ClayClean }

[Tool, GlobalClass]
public partial class S3Environment : Resource
{
    [Export] public Color BackgroundColor { get; set; } = new(0.28f, 0.32f, 0.38f);
    [Export] public Color AmbientColor { get; set; } = Colors.White;
    [Export] public float AmbientEnergy { get; set; } = 0.2f;
    [Export] public Color FogColor { get; set; } = new(0.28f, 0.32f, 0.38f);
    [Export] public float FogDensity { get; set; }
    [Export] public S3ShadingMode ShadingMode { get; set; }
    [Export] public bool EquatorialGroundEnabled { get; set; } = true;
    [Export] public StandardMaterial3D? GroundMaterial { get; set; }

    public EnvironmentBlock Capture(int groundMaterial)
    {
        var scale = GroundMaterial?.Uv1Scale ?? Vector3.One;
        var offset = GroundMaterial?.Uv1Offset ?? Vector3.Zero;
        return new()
        {
            Background = GlomeBuffers.Rgba(BackgroundColor.SrgbToLinear()),
            AmbientEnergy = GlomeBuffers.Rgb(AmbientColor.SrgbToLinear(), AmbientEnergy),
            FogDensity = GlomeBuffers.Rgb(FogColor.SrgbToLinear(), FogDensity),
            ModeGroundMaterial = new((int)ShadingMode, EquatorialGroundEnabled ? groundMaterial : -1, 0, 0),
            GroundUvTransform = new(0.25f*scale.X, 0.25f*scale.Y, offset.X, offset.Y)
        };
    }
}
