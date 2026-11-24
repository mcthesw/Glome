#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Glome.Core;
using Godot;
using Godot.Collections;
using RD = Godot.RenderingDevice;

namespace Glome;

public sealed class GlomeComputeRenderer : IDisposable
{
    public const double VerticalFov = 70;
    public Texture2Drd Output { get; } = new();
    public ConcurrentQueue<RenderNotice> Notices { get; } = new();

    private readonly S3RenderProgram _program;
    // Only the render thread touches device state and the two cached output sizes.
    private RenderingDevice? _rd;
    private Rid _shader, _faces, _tree, _materials, _lights, _environment, _probes, _image, _set;
    private readonly Rid[] _samplers = new Rid[4], _banks = new Rid[4];
    private Rid _programSet;
    private Rid[] _programBuffers = [];
    private readonly Rid[] _pipelines;
    private Vector2I _size, _spareSize;
    private Rid _spareImage, _spareSet;
    private bool _failed;
    private bool _disposed;
    private byte[]? _probeRecords;
    private readonly System.Collections.Generic.SortedDictionary<string, double> _viewMs = new();
    private string _timingPrefix = "";
    private double _probeMs, _readbackMs;
    private int _timedViews, _timingPolls;
    private readonly bool _capture;

    private int _probeGeneration;

    public GlomeComputeRenderer(S3BakeSnapshot snapshot, Vector2I size, S3RenderProgram? program = null,
                                byte[]? probes = null, bool capture = false, string timingPrefix = "")
        : this(GlomeBuffers.Pack(snapshot), size, program, probes, capture, timingPrefix) { }

    public GlomeComputeRenderer(GlomeBuffers buffers, Vector2I size, S3RenderProgram? program = null,
                                byte[]? probes = null, bool capture = false, string timingPrefix = "")
    {
        _program = program ?? new GlomeStandardProgram();
        var spirv = _program.GetComputeSpirV();
        var values = (int[])_program.SpecializationValues.Clone();
        var bindings = _program.CaptureSet1?.Invoke() ?? [];
        _pipelines = new Rid[Math.Max(1, values.Length)];
        _capture = capture;
        _timingPrefix = timingPrefix;
        Schedule(() =>
        {
            _rd = RenderingServer.GetRenderingDevice();
            if (_rd is null)
            {
                Notices.Enqueue(new(RenderNoticeKind.NoDevice, "No Vulkan RenderingDevice."));
                return;
            }
            _shader = Valid(_rd.ShaderCreateFromSpirV(spirv, "Glome"), "shader");
            for (int i = 0; i < _pipelines.Length; i++)
            {
                var constants = new Array<RDPipelineSpecializationConstant>();
                if (values.Length != 0) constants.Add(new() { ConstantId = 0, Value = values[i] });
                _pipelines[i] = Valid(_rd.ComputePipelineCreate(_shader, constants), "compute pipeline");
            }
            for (int i = 0; i < _samplers.Length; i++)
            {
                var filter = (i & 1) == 0 ? RD.SamplerFilter.Nearest : RD.SamplerFilter.Linear;
                var repeat = i < 2 ? RD.SamplerRepeatMode.Repeat : RD.SamplerRepeatMode.ClampToEdge;
                _samplers[i] = Valid(_rd.SamplerCreate(new RDSamplerState
                {
                    MinFilter = filter, MagFilter = filter, MipFilter = RD.SamplerFilter.Linear,
                    RepeatU = repeat, RepeatV = repeat, MaxLod = 32
                }), "material sampler");
            }
            _probes = Buffer(probes ?? new byte[64]);
            Upload(buffers, bindings);
            ResizeImage(size);
        });
    }

    private static Rid Valid(Rid rid, string resource) =>
        rid.IsValid ? rid : throw new InvalidOperationException($"Could not create {resource}.");

    private void Schedule(Action action) => RenderingServer.CallOnRenderThread(Callable.From(() =>
    {
        if (_failed) return;
        try { action(); }
        catch (Exception e)
        {
            _failed = true;
            Notices.Enqueue(new(RenderNoticeKind.Error, e.ToString()));
        }
    }));

    private Rid Buffer(byte[] data) => Valid(_rd!.StorageBufferCreate((uint)data.Length, data), "storage buffer");
    private void Free(ref Rid rid)
    {
        if (rid.IsValid) _rd!.FreeRid(rid);
        rid = default;
    }

    public void Rebuild(GlomeBuffers scene, byte[]? probes = null, string timingPrefix = "")
    {
        var bindings = _program.CaptureSet1?.Invoke() ?? [];
        Schedule(() =>
        {
            if (_rd is null) return;
            Free(ref _spareSet);
            Free(ref _set);
            if (probes is not null)
            {
                Free(ref _probes);
                _probes = Buffer(probes);
            }
            _probeGeneration++;
            _probeRecords = null;
            _timingPrefix = timingPrefix;
            _viewMs.Clear();
            _probeMs = 0;
            _timedViews = 0;
            _timingPolls = 0;
            Upload(scene, bindings);
            Bind();
        });
    }

    private void ReleaseScene()
    {
        Free(ref _programSet);
        for (int i = 0; i < _programBuffers.Length; i++) Free(ref _programBuffers[i]);
        for (int i = 0; i < _banks.Length; i++) Free(ref _banks[i]);
        Free(ref _faces); Free(ref _tree); Free(ref _materials); Free(ref _environment); Free(ref _lights);
    }

    private void Upload(GlomeBuffers scene, ProgramBuffer[] bindings)
    {
        ReleaseScene();
        _faces = Buffer(scene.Triangles);
        _tree = Buffer(scene.Nodes);
        _materials = Buffer(scene.Materials);
        _lights = Buffer(scene.Lights);
        _environment = Valid(_rd!.UniformBufferCreate(80, GlomeBuffers.Bytes(scene.Environment)), "environment");
        for (int i = 0; i < _banks.Length; i++)
        {
            var bank = i < scene.Banks.Length ? scene.Banks[i] : TextureBank.Empty;
            ulong limit = _rd.LimitGet(RD.Limit.MaxTextureSize2D);
            if ((ulong)bank.Width > limit || (ulong)bank.Height > limit ||
                (ulong)bank.Layers.Length > _rd.LimitGet(RD.Limit.MaxTextureArrayLayers))
                throw new InvalidOperationException($"Texture bank {i} exceeds device limits ({bank.Width}x{bank.Height}, {bank.Layers.Length} layers).");
            var format = new RDTextureFormat
            {
                Format = bank.Srgb ? RD.DataFormat.R8G8B8A8Srgb : RD.DataFormat.R8G8B8A8Unorm,
                TextureType = RD.TextureType.Type2DArray,
                Width = (uint)bank.Width, Height = (uint)bank.Height, Depth = 1,
                ArrayLayers = (uint)bank.Layers.Length, Mipmaps = (uint)bank.MipCount,
                UsageBits = RD.TextureUsageBits.SamplingBit
            };
            _banks[i] = Valid(_rd.TextureCreate(format, new RDTextureView(), new Array<byte[]>(bank.Layers)), $"texture bank {i}");
        }
        if (bindings.Length == 0) return;
        var uniforms = new Array<RDUniform>();
        _programBuffers = new Rid[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            _programBuffers[i] = binding.Type switch
            {
                RD.UniformType.UniformBuffer => Valid(_rd.UniformBufferCreate((uint)binding.Data.Length, binding.Data), "program UBO"),
                RD.UniformType.StorageBuffer => Buffer(binding.Data),
                _ => throw new ArgumentException($"Unsupported program buffer type {binding.Type}.")
            };
            Add(uniforms, binding.Type, _programBuffers[i]);
        }
        _programSet = Valid(_rd.UniformSetCreate(uniforms, _shader, 1), "program uniform set");
    }

    private void Update(Rid buffer, uint offset, byte[] data)
    {
        if (_rd is null) return;
        var err = _rd.BufferUpdate(buffer, offset, (uint)data.Length, data);
        if (err != Error.Ok) throw new InvalidOperationException($"Buffer update failed ({err}).");
    }

    public void UpdateProgramBuffer(int binding, byte[] snapshot) => Schedule(() =>
    {
        if (_rd != null) Update(_programBuffers[binding], 0, snapshot);
    });

    public void UpdateLight(int index, LightRecord record) =>
        Schedule(() => Update(_lights, checked((uint)(16 + 32 * index)), GlomeBuffers.Bytes(record)));

    public void UpdateEnvironment(EnvironmentBlock environment) =>
        Schedule(() => Update(_environment, 0, GlomeBuffers.Bytes(environment)));

    public void Resize(Vector2I size) => Schedule(() => { if (_rd is not null) ResizeImage(size); });

    private void ResizeImage(Vector2I size)
    {
        if (_image.IsValid && _size == size) return;
        if (_spareImage.IsValid && _spareSize == size)
        {
            (_image, _spareImage) = (_spareImage, _image);
            (_set, _spareSet) = (_spareSet, _set);
            (_size, _spareSize) = (_spareSize, _size);
            Output.TextureRdRid = _image;
            if (!_set.IsValid) Bind();
            return;
        }
        Free(ref _spareSet);
        Free(ref _spareImage);
        (_spareImage, _spareSet, _spareSize) = (_image, _set, _size);
        _set = default;
        _image = Valid(_rd!.TextureCreate(new RDTextureFormat
        {
            Format = RD.DataFormat.R8G8B8A8Unorm,
            TextureType = RD.TextureType.Type2D,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RD.TextureUsageBits.StorageBit | RD.TextureUsageBits.SamplingBit |
                        (_capture ? RD.TextureUsageBits.CanCopyFromBit : 0)
        }, new RDTextureView()), "output texture");
        Output.TextureRdRid = _image;
        _size = size;
        Bind();
    }

    private static void Add(Array<RDUniform> uniforms, RD.UniformType type, Rid resource)
    {
        var uniform = new RDUniform { UniformType = type, Binding = uniforms.Count };
        uniform.AddId(resource);
        uniforms.Add(uniform);
    }

    private void Bind()
    {
        var uniforms = new Array<RDUniform>();
        Add(uniforms, RD.UniformType.Image, _image);
        foreach (var buffer in new[] { _faces, _tree, _materials, _probes, _lights })
            Add(uniforms, RD.UniformType.StorageBuffer, buffer);
        Add(uniforms, RD.UniformType.UniformBuffer, _environment);
        foreach (var bank in _banks) Add(uniforms, RD.UniformType.Texture, bank);
        foreach (var sampler in _samplers) Add(uniforms, RD.UniformType.Sampler, sampler);
        _set = Valid(_rd!.UniformSetCreate(uniforms, _shader, 0), "scene uniform set");
    }

    public void Draw(S3Frame view, double radius, int nodeCount, float elapsedSeconds, int probeCount = 0,
                     string? timingLabel = null, int pipeline = 0, double verticalFov = VerticalFov)
    {
        Schedule(() =>
        {
            if (_rd is null) return;
            var camera = new CameraPush
            {
                Right = GlomeBuffers.Vec(view.Right), Up = GlomeBuffers.Vec(view.Up),
                Back = GlomeBuffers.Vec(view.Back), Pos = GlomeBuffers.Vec(view.P),
                Lens = new((float)radius, (float)Math.Tan(double.DegreesToRadians(verticalFov) * 0.5), elapsedSeconds, 0),
                Dims = new(_size.X, _size.Y, nodeCount, probeCount)
            };
            var push = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref camera, 1));
            string? marker = timingLabel is null ? null : _timingPrefix + timingLabel;
            long start = Stopwatch.GetTimestamp();
            if (marker is not null)
            {
                if (probeCount == 0) _timedViews++;
                _rd.CaptureTimestamp(marker + "_begin");
            }
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipelines[pipeline]);
            if (_programSet.IsValid) _rd.ComputeListBindUniformSet(list, _programSet, 1);
            _rd.ComputeListBindUniformSet(list, _set, 0);
            _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
            uint w = (uint)(probeCount == 0 ? _size.X : probeCount);
            uint h = (uint)(probeCount == 0 ? _size.Y : 1);
            _rd.ComputeListDispatch(list, (w + 7) / 8, (h + 7) / 8, 1);
            _rd.ComputeListEnd();
            if (marker is not null) _rd.CaptureTimestamp(marker + "_end");
            if (probeCount == 0) return;

            // The sole device readback is opt-in. The global RD submits via the frame loop.
            int generation = _probeGeneration;
            var err = _rd.BufferGetDataAsync(_probes, Callable.From<byte[]>(data =>
            {
                if (generation != _probeGeneration) return;
                _readbackMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _probeRecords = data;
            }));
            if (err != Error.Ok) throw new InvalidOperationException($"Probe readback failed ({err}).");
        });
    }

    public void PollProbe() => Schedule(() =>
    {
        if (_rd is null) return;
        ulong start = 0;
        for (uint i = 0; i < _rd.GetCapturedTimestampsCount(); i++)
        {
            ulong ns = _rd.GetCapturedTimestampGpuTime(i);
            string name = _rd.GetCapturedTimestampName(i);
            if (!name.StartsWith(_timingPrefix, StringComparison.Ordinal)) continue;
            if (name.EndsWith("_begin", StringComparison.Ordinal)) { start = ns; continue; }
            if (start == 0 || !name.EndsWith("_end", StringComparison.Ordinal)) continue;
            string label = name[_timingPrefix.Length..^4];
            double ms = (ns-start)*1e-6;
            if (label == "probe") _probeMs = ms;
            else _viewMs[label] = ms;
            start = 0;
        }
        if (_probeRecords is null) return;
        bool complete = _timedViews > 0 && _viewMs.Count == _timedViews && _probeMs > 0;
        if (!complete && ++_timingPolls < 12) return;
        string gpu = complete
            ? $"GPU frame dispatch {_viewMs.Values.Max():F3} ms peak ({string.Join(", ", _viewMs.Select(v => $"{v.Key}={v.Value:F3}"))}), probe dispatch {_probeMs:F3} ms"
            : "GPU timestamps unavailable";
        Notices.Enqueue(new(RenderNoticeKind.Probe, $"{gpu}, dispatch to readback {_readbackMs:F2} ms", _probeRecords));
        _probeRecords = null;
    });

    public void SaveFrame(string path) => Schedule(() =>
    {
        var pixels = _rd!.TextureGetData(_image, 0);
        using var image = Image.CreateFromData(_size.X, _size.Y, false, Image.Format.Rgba8, pixels);
        var error = image.SavePng(path);
        if (error != Error.Ok) throw new InvalidOperationException($"Cannot save {path} ({error}).");
        Notices.Enqueue(new(RenderNoticeKind.FrameSaved, path));
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            Free(ref _spareSet);
            Free(ref _set);
            if (_image.IsValid) Output.TextureRdRid = default;
            Free(ref _spareImage);
            Free(ref _image);
            ReleaseScene();
            Free(ref _probes);
            for (int i = 0; i < _samplers.Length; i++) Free(ref _samplers[i]);
            for (int i = 0; i < _pipelines.Length; i++) Free(ref _pipelines[i]);
            Free(ref _shader);
            Output.Dispose();
        }));
    }
}

public enum RenderNoticeKind { Error, NoDevice, Probe, FrameSaved }
public readonly record struct RenderNotice(RenderNoticeKind Kind, string Message, byte[]? Data = null);
