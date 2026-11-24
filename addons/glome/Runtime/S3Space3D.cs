#nullable enable
using Godot;
using Glome.Core;
using System;
using System.Linq;

namespace Glome;

public sealed record S3BakeSnapshot(long Revision, BakedGeometry Geometry,
                                    S3CollisionWorld Collision, Node3D[]? TriangleOwners, GlomeMaterials? Materials,
                                    GlomeLight[] Lights, EnvironmentBlock Environment);

[Tool, GlobalClass]
public partial class S3Space3D : Node3D
{
    private double _radius = 1;

    [Export]
    public double Radius
    {
        get => _radius;
        set
        {
            if (!double.IsFinite(value)) return;
            double radius = ConstrainRadius(value);
            if (!double.IsFinite(radius) || radius <= 0 || _radius == radius) return;
            _radius = radius;
            OnRadiusChanged();
            RequestRebake();
        }
    }

    [Export] public bool EquatorialGroundEnabled { get; set; } = true;
    [Export] public S3Environment Environment { get; set; } = new();
    [Export] public bool AutoStart { get; set; } = true;
    [Export] public S3RenderProgram? RenderProgram { get; set; }
    [Export] public TextureRect? OutputTarget { get; set; }
    [Export] public ShaderMaterial? OutputMaterial { get; set; }
    [Export(PropertyHint.Range, "0.1,2,0.05,or_greater")] public float ResolutionScale { get; set; } = 1;
    [Export] public CanvasItem.TextureFilterEnum DisplayFilter { get; set; } = CanvasItem.TextureFilterEnum.Linear;
    [Export] public BakeVisibility BakeVisibilityPolicy { get; set; } = BakeVisibility.HonoredUpToRoot;

    private GlomeStandaloneSession? _session;
    private bool _rebake;
    public Texture2D? OutputTexture => _session?.Output;

    internal S3Camera3D? CurrentCamera() => GlomeChart.Descendants(this).OfType<S3Camera3D>().FirstOrDefault(c => c.Current);
    internal S3Frame DefaultEye() => new WalkPose(S3Frame.Identity, 0).Eye(Radius, 1.65);
    internal S3Frame InitialView() => CurrentCamera()?.EyeFrame(Radius) ?? DefaultEye();

    internal void CamerasChanged(S3Camera3D? camera) => _session?.CamerasChanged(camera);
    internal event Action? BakeInvalidated;
    public void RequestRebake()
    {
        _rebake = _session != null;
        BakeInvalidated?.Invoke();
    }

    public override void _Ready()
    {
        if (!AutoStart || Engine.IsEditorHint()) return;
        _session = new(this);
        try { _session.Start(); }
        catch (Exception e) { StopSession(); GD.PushError(e.ToString()); }
    }

    public override void _Process(double delta)
    {
        if (_session == null) return;
        try
        {
            if (_rebake) { _rebake = false; _session.Rebake(); }
            _session.Process(delta);
        }
        catch (Exception e) { StopSession(); GD.PushError(e.ToString()); }
    }

    private void StopSession()
    {
        _session?.Dispose();
        _session = null;
        _rebake = false;
    }

    public override void _ExitTree()
    {
        StopSession();
        RequestReady();
    }

    // Only a completed bake advances the revision and replaces the published snapshot.
    public S3BakeSnapshot? LastBake { get; private set; }
    public long BakeRevision => LastBake?.Revision ?? 0;

    protected virtual double ConstrainRadius(double value) => value;
    internal double EffectiveRadius(double value) => ConstrainRadius(value);
    protected virtual void OnRadiusChanged() { }

    internal S3BakeSnapshot Publish(BakedGeometry geometry, S3CollisionWorld collision, Node3D[]? owners, GlomeMaterials? materials,
                                    GlomeLight[] lights, EnvironmentBlock environment) =>
        LastBake = new(BakeRevision + 1, geometry, collision, owners, materials, lights, environment);
}
