#nullable enable
using Godot;

namespace Glome;

[Tool, GlobalClass]
public partial class GlomeStandardProgram : S3RenderProgram
{
    public GlomeStandardProgram()
    {
        Shader = ResourceLoader.Load<RDShaderFile>("res://addons/glome/Shaders/glome_standard.glsl");
    }
}
