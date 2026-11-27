#nullable enable
using System;
using Godot;

namespace Glome;

// Bindings are numbered consecutively from zero. Capture runs on the submitting
// thread; its arrays are immutable for the lifetime of the queued upload.
public readonly record struct ProgramBuffer(RenderingDevice.UniformType Type, byte[] Data);

[Tool, GlobalClass]
public partial class S3RenderProgram : Resource
{
    [Export] public RDShaderFile? Shader { get; set; }
    [Export] public int[] SpecializationValues { get; set; } = [];
    // Optional effects compile on first use. Existing programs prewarm every variant.
    public int PrewarmPipelineCount { get; init; } = int.MaxValue;
    public Func<ProgramBuffer[]>? CaptureSet1 { get; set; }

    internal RDShaderSpirV GetComputeSpirV()
    {
        var file = Shader ?? throw new InvalidOperationException("Missing compute shader.");
        if (!string.IsNullOrEmpty(file.BaseError)) throw new InvalidOperationException(file.BaseError);
        var spirv = file.GetSpirV();
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException(spirv.CompileErrorCompute);
        if (spirv.BytecodeCompute.Length == 0) throw new InvalidOperationException($"No compute stage in {file.ResourcePath}.");
        return spirv;
    }
}
