#nullable enable
using Glome.Core;
using Godot;

namespace Glome;

public static class CollisionShapeAdapter
{
    public static CollisionProxy? Create(S3Frame chart, double radius, Transform3D pose, Shape3D? shape) =>
        shape switch
        {
            BoxShape3D box => Box(chart, radius, pose, box.Size / 2),
            CylinderShape3D cylinder => Create(chart, radius, pose,
                new(cylinder.Radius, cylinder.Height / 2, cylinder.Radius), ProxyShape.Cylinder),
            _ => null
        };

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
