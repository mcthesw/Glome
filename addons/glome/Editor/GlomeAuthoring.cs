#nullable enable
#if TOOLS
using System;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal static class GlomeAuthoring
{
    internal readonly record struct Extent(double ChartMetres, double AroundPivot)
    {
        public bool Distorted(double radius) => ChartMetres > radius / 3;
        public double Compression(double radius)
        {
            double ratio = ChartMetres / radius;
            return 100 * ratio * ratio / (1 + ratio * ratio);
        }
    }

    internal static S3Anchor3D? AnchorOf(Node? node)
    {
        for (; node is not null && node is not S3Space3D and not Viewport; node = node.GetParent())
            if (node is S3Anchor3D anchor) return anchor;
        return null;
    }

    internal static S3Frame Pivot(Node3D node, double radius)
    {
        var (frame, transform) = GlomeNodeEditing.Pose(node, radius);
        if (Mathf.IsZeroApprox(transform.Basis.Determinant())) transform.Basis = Basis.Identity;
        return ChartCamera.FromTangent(frame, transform, radius);
    }

    internal static Extent Measure(Node3D root, double radius)
    {
        var (_, transform) = GlomeNodeEditing.Pose(root, radius);
        double chartSquared = 0, pivotSquared = 0;
        void Inspect(Node node, Transform3D pose)
        {
            if (node is CsgShape3D csg && (node == root || csg.IsRootShape()))
            {
                var bounds = csg.GetAabb();
                for (int i = 0; i < 8; i++) Include(pose * bounds.GetEndpoint(i));
            }
            if (node is not MeshInstance3D { Mesh: { } mesh }) return;
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                using var arrays = mesh.SurfaceGetArrays(i);
                foreach (var vertex in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    Include(pose * vertex);
            }
        }
        void Include(Vector3 point)
        {
            chartSquared = Math.Max(chartSquared, point.LengthSquared());
            pivotSquared = Math.Max(pivotSquared, point.DistanceSquaredTo(transform.Origin));
        }
        Inspect(root, transform);
        GlomeChart.Walk(root, transform, Inspect);
        return new(Math.Sqrt(chartSquared), Math.Sqrt(pivotSquared));
    }

    internal static S3Frame Focus(S3Frame target, double radius, double extent, double fov)
    {
        double metres = Math.Max(4.85, extent * 1.2 / Math.Sin(double.DegreesToRadians(fov / 2)));
        double pitch = Math.Atan2(1.8, 4.5);
        var direction = target.Back * Math.Cos(pitch) + target.Up * Math.Sin(pitch);
        return target.Travel(direction, Math.Min(metres / radius, Math.PI * 0.40)).Pitch(-pitch);
    }

    internal static S3Frame Opposite(S3Frame f) => new(-f.Right, -f.Up, -f.Back, -f.P);

    internal static S3Frame Ground(S3Frame f)
    {
        // At a geographic pole every meridian reaches ground at the same distance.
        double latitude = Math.Asin(Math.Clamp(f.P.Y, -1, 1));
        var south = (-D4.Up * Math.Sign(latitude)).Reject(f.P);
        if (Math.Abs(latitude) < 1e-12) return f;
        return f.Travel(south.Length > 1e-10 ? south.Unit() : -f.Back, Math.Abs(latitude));
    }

    internal static S3Frame Level(S3Frame f)
    {
        var up = D4.Up.Reject(f.P);
        if (up.Length < 1e-10) up = D4.Pole.Reject(f.P);
        up = up.Unit();
        var back = f.Back.Reject(up);
        if (back.Length < 1e-10) back = -D4.Hodge(f.Right, up, f.P);
        back = back.Unit();
        return new(-D4.Hodge(up, back, f.P), up, back, f.P);
    }

    internal static S3Frame FaceOrigin(S3Frame f)
    {
        var target = f.P.AngleTo(D4.Pole) < 0.01 ? -D4.Pole : D4.Pole;
        var back = (-target).Reject(f.P);
        // Every meridian reaches the conjugate point. Keep the horizontal heading.
        if (back.Length < 1e-10) return Level(f);
        back = back.Unit();
        var up = f.Up.Reject(back);
        if (up.Length < 1e-10) up = D4.Hodge(f.Right, back, f.P);
        up = up.Unit();
        return new(-D4.Hodge(up, back, f.P), up, back, f.P);
    }

    internal static double ArcStep(double metres, bool snap) => snap ? Round(metres, 0.5) : metres;
    internal static double AngleStep(double radians, bool snap) => snap ? Round(radians, Math.PI / 12) : radians;
    private static double Round(double value, double step) =>
        Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    // Apply a world isometry without discarding an inherited chart, local scale, or offset.
    internal static double[] DisplacedSpin(S3Anchor3D anchor, S3Frame delta, double radius) =>
        S3ChartMath.Encode(S3ChartMath.Compose(S3ChartMath.Transpose(anchor.BasePose(radius).Frame),
            S3ChartMath.Compose(delta, anchor.ChartPose(radius).Frame)));

    internal static S3Frame Between(S3Frame from, S3Frame to) =>
        S3ChartMath.Compose(to, S3ChartMath.Transpose(from));
}
#endif
