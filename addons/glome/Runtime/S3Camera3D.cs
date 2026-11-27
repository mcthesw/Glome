#nullable enable
using Godot;
using Glome.Core;

namespace Glome;

[Tool, GlobalClass]
public partial class S3Camera3D : Node3D
{
    [Export(PropertyHint.Range, "1,179,degrees")] public float Fov { get; set; } = 70;
    private bool _current = true;
    [Export] public bool Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            GlomeChart.SpaceOf(this)?.CamerasChanged(this);
        }
    }

    internal S3Frame? DrivenEye { get; set; }

    public S3Frame EyeFrame(double radius)
    {
        var (frame, parent) = Mount(radius);
        return ChartCamera.FromTangent(frame, parent * Transform, radius);
    }

    internal (S3Frame Frame, Transform3D Pose) Mount(double radius)
    {
        if (GetParent() is not S3CharacterController controller) return GlomeChart.ParentPose(GetParent(), radius);
        var basis = GlomeChart.InheritedPose(controller, radius).Pose.Basis;
        return (DrivenEye ?? controller.AuthoredEye(radius), new(basis.Orthonormalized().Inverse() * basis, Vector3.Zero));
    }

    public override void _EnterTree() => GlomeChart.SpaceOf(this)?.CamerasChanged(this);
    public override void _ExitTree() => GlomeChart.SpaceOf(this)?.CamerasChanged(null);
}
