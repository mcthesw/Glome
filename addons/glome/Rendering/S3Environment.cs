#nullable enable
using Godot;

namespace Glome;

public enum S3ShadingMode { StandardLit, Unlit, ClayClean }

[Tool, GlobalClass]
public partial class S3Environment : Resource
{
    internal static readonly Color DefaultBackground = new(0.28f, 0.32f, 0.38f);
    internal const float DefaultAmbientEnergy = .2f;
    [Export] public Color BackgroundColor { get; set; } = DefaultBackground;
    [Export] public Color AmbientColor { get; set; } = Colors.White;
    [Export] public float AmbientEnergy { get; set; } = DefaultAmbientEnergy;
    [Export] public Color FogColor { get; set; } = DefaultBackground;
    [Export] public float FogDensity { get; set; }
    [Export] public S3ShadingMode ShadingMode { get; set; }
    [Export] public bool EquatorialGroundEnabled { get; set; } = true;
    [Export] public StandardMaterial3D? GroundMaterial { get; set; }

    internal static EnvironmentBlock CaptureOrDefault(S3Environment? environment, int groundMaterial)
    {
        if (environment != null) return environment.Capture(groundMaterial);
        using var defaults = new S3Environment();
        return defaults.Capture(groundMaterial);
    }

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
