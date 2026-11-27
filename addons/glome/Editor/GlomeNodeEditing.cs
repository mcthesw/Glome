#nullable enable
#if TOOLS
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal static class GlomeNodeEditing
{
    // Resolve outer locked instances first, even if an inner child belongs to this scene.
    internal static Node3D? EditableTarget(Node? source, Node? scene)
    {
        if (source == null || scene == null || !GodotObject.IsInstanceValid(source)) return null;
        Node3D? target = null;
        for (var n = source; n != null && n != scene; n = n.GetParent())
        {
            if (n is Viewport) return null;
            if (n is not Node3D spatial || n.Owner == null) continue;
            if (n != source && n.SceneFilePath.Length != 0 && !scene.IsEditableInstance(n)) target = spatial;
            else if (target == null && (n.Owner == scene || scene.IsEditableInstance(n.Owner))) target = spatial;
        }
        return target != null && scene.IsAncestorOf(target) ? target : null;
    }

    internal static (S3Frame Frame, Transform3D Pose) Parent(Node3D node, double radius) =>
        node is S3Camera3D camera ? camera.Mount(radius) : GlomeChart.ParentPose(node.GetParent(), radius);

    internal static (S3Frame Frame, Transform3D Pose) Pose(Node3D node, double radius)
    {
        if (node is S3Anchor3D anchor) return anchor.ChartPose(radius);
        var (chart, parent) = Parent(node, radius);
        return (chart, parent * node.Transform);
    }

    internal static bool TryLocalPoint(S3Frame chart, Transform3D parent, double radius, D4 point, out Vector3 local)
    {
        local = default;
        if (!Invertible(parent.Basis) || !S3ChartMath.TryInverseCentralProjection(chart, radius, point, out var metres)) return false;
        local = parent.AffineInverse() * new Vector3((float)metres.X, (float)metres.Y, (float)metres.Z);
        return local.IsFinite();
    }

    internal static bool Invertible(Basis basis) => basis.IsFinite() && System.Math.Abs(basis.Determinant()) > 1e-9f;

    internal static D4 Lift(S3Frame chart, Vector3 metres, double radius) =>
        S3ChartMath.CentralProjection(chart, radius, chart.Local(metres.X, metres.Y, metres.Z), out _);
}
#endif
