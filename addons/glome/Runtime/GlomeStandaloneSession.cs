#nullable enable
using System;
using Godot;

namespace Glome;

internal sealed class GlomeStandaloneSession(S3Space3D space) : IDisposable
{
    private S3Camera3D? _camera;
    private S3CharacterController? _controller;
    private bool _resolveCamera = true;
    private S3Camera3D? _preferred;
    private Camera3D? _native, _previous;
    private CanvasLayer? _layer;
    private TextureRect? _target;
    private Texture2D? _oldTexture;
    private Material? _oldMaterial;
    private CanvasItem.TextureFilterEnum _oldFilter;
    private GlomeComputeRenderer? _renderer;
    private S3BakeSnapshot _bake = null!;
    private Vector2I _size;
    private double _elapsed;

    internal Texture2D? Output => _renderer?.Output;

    internal void CamerasChanged(S3Camera3D? preferred)
    {
        _preferred = preferred;
        _resolveCamera = true;
    }

    private void BindCamera()
    {
        var active = GodotObject.IsInstanceValid(_preferred) && _preferred!.IsInsideTree() && _preferred.Current
            ? _preferred : space.CurrentCamera();
        if (_camera != active && GodotObject.IsInstanceValid(_controller)) _controller!.Unbind();
        _camera = active;
        _controller = active?.GetParent() as S3CharacterController;
        _controller?.Bind(active!, _bake.Collision);
        _resolveCamera = false;
    }

    internal void Start()
    {
        Rebake();
        var viewport = space.GetViewport();
        _previous = viewport.GetCamera3D();
        _native = new Camera3D { Name = "GlomeCamera", CullMask = 0, TopLevel = true };
        space.AddChild(_native, false, Node.InternalMode.Back);
        _native.GlobalTransform = Transform3D.Identity;
        _native.MakeCurrent();

        if (DisplayServer.GetName() == "headless") return;
        var device = RenderingServer.GetRenderingDevice();
        if (device == null || device.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupInvocations) < 64)
            throw new NotSupportedException("Glome needs a compute-capable RenderingDevice with 8x8 workgroups. Select Forward+ or Mobile.");
        _target = space.OutputTarget;
        if (_target == null)
        {
            _layer = new CanvasLayer { Name = "GlomeDisplay", Layer = -1 };
            space.AddChild(_layer, false, Node.InternalMode.Back);
            _target = new TextureRect
            {
                Name = "Output", MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize
            };
            _layer.AddChild(_target);
            _target.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _oldTexture = _target.Texture;
        _oldMaterial = _target.Material;
        _oldFilter = _target.TextureFilter;
        _size = TargetSize();
        _renderer = new(_bake, _size, space.RenderProgram);
        _target.Texture = _renderer.Output;
        ApplyPresentation();
    }

    internal void Rebake()
    {
        _bake = GlomeBaker.Bake(space, visibility: space.BakeVisibilityPolicy);
        BindCamera();
        _renderer?.Rebuild(GlomeBuffers.Pack(_bake));
    }

    private Vector2I TargetSize()
    {
        var toPixels = _target!.GetViewport().GetFinalTransform() * _target.GetGlobalTransformWithCanvas();
        var pixels = _layer != null ? (Vector2)space.GetViewport().GetTexture().GetSize() : _target.Size * toPixels.Scale.Abs();
        return new(Math.Max(1, Mathf.RoundToInt(pixels.X * space.ResolutionScale)),
                   Math.Max(1, Mathf.RoundToInt(pixels.Y * space.ResolutionScale)));
    }

    private void ApplyPresentation()
    {
        if (_target!.Material != space.OutputMaterial) _target.Material = space.OutputMaterial;
        if (_target.TextureFilter != space.DisplayFilter) _target.TextureFilter = space.DisplayFilter;
    }

    internal void Process(double delta)
    {
        if (_resolveCamera) BindCamera();
        if (_renderer == null) return;
        if (_renderer.Notices.TryDequeue(out var notice)) throw new InvalidOperationException(notice.Message);
        if (!GodotObject.IsInstanceValid(_target)) return;
        var size = TargetSize();
        if (_size != size) { _renderer.Resize(size); _size = size; }
        ApplyPresentation();
        _elapsed += delta;
        var view = _camera?.EyeFrame(space.Radius) ?? space.DefaultEye();
        _renderer.Draw(view, space.Radius, _bake.Geometry.Nodes.Length, (float)_elapsed,
            verticalFov: Math.Clamp(_camera?.Fov ?? 70, 1, 179));
    }

    public void Dispose()
    {
        if (GodotObject.IsInstanceValid(_controller)) _controller!.Unbind();
        if (GodotObject.IsInstanceValid(_target))
        {
            _target!.Texture = _oldTexture;
            _target.Material = _oldMaterial;
            _target.TextureFilter = _oldFilter;
        }
        _renderer?.Dispose();
        _layer?.QueueFree();
        if (GodotObject.IsInstanceValid(_previous) && _previous!.IsInsideTree() &&
            (space.GetViewport()?.GetCamera3D() is not { } active || active == _native))
            _previous.MakeCurrent();
        _native?.QueueFree();
    }
}
