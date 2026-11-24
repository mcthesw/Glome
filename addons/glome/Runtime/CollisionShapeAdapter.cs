#nullable enable
using Glome.Core;
using Godot;

namespace Glome;

public static class CollisionShapeAdapter
{
    internal readonly record struct Input(S3Frame Chart, Transform3D Pose, Vector3 Half, ProxyShape Shape)
    {
        internal CollisionProxy Bake(double radius) => Create(Chart, radius, Pose, Half, Shape);
    }

    internal static Input? Capture(S3Frame chart, Transform3D pose, Shape3D? shape) =>
        shape switch
        {
            BoxShape3D box => new(chart, pose, box.Size / 2, ProxyShape.Box),
            CylinderShape3D cylinder => new(chart, pose,
                new(cylinder.Radius, cylinder.Height / 2, cylinder.Radius), ProxyShape.Cylinder),
            _ => null
        };

    public static CollisionProxy? Create(S3Frame chart, double radius, Transform3D pose, Shape3D? shape) =>
        Capture(chart, pose, shape)?.Bake(radius);

    public static CollisionProxy Box(S3Frame chart, double radius, Transform3D pose, Vector3 halfExtents) =>
        Create(chart, radius, pose, halfExtents, ProxyShape.Box);

    private static CollisionProxy Create(S3Frame chart, double radius, Transform3D pose, Vector3 half, ProxyShape shape)
    {
        var inverse = pose.AffineInverse();
        static D4 Row(Vector3 row, float offset) => new(row.X, row.Y, row.Z, offset);
        var transform = new ProxyTransform(Row(inverse.Basis.Row0, inverse.Origin.X),
            Row(inverse.Basis.Row1, inverse.Origin.Y), Row(inverse.Basis.Row2, inverse.Origin.Z),
            new(pose.Basis.X.Length(), pose.Basis.Y.Length(), pose.Basis.Z.Length(), 0), pose.Origin.Length());
        return new(chart, radius, transform, new(half.X, half.Y, half.Z, 0), shape);
    }
}
