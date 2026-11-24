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
        if (GetParent() is not S3CharacterController controller) return GlomeChart.At(this, radius);
        var eye = DrivenEye ?? controller.AuthoredEye(radius);
        if (Transform == Transform3D.Identity) return eye;
        // The controller supplies eye height. The camera can add a local mount and rotation.
        var basis = GlomeChart.InheritedPose(controller, radius).Pose.Basis;
        var mount = new Transform3D(basis.Orthonormalized().Inverse() * basis, Vector3.Zero) * Transform;
        return ChartCamera.FromTangent(eye, mount, radius);
    }

    public override void _EnterTree() => GlomeChart.SpaceOf(this)?.CamerasChanged(this);
    public override void _ExitTree() => GlomeChart.SpaceOf(this)?.CamerasChanged(null);
}
