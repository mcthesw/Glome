#nullable enable
using System;
using Godot;
using Glome.Core;

namespace Glome;

public static class ChartCamera
{
    // The editor unfolds radial distance along XZ and altitude along Y, in metres.
    // Undo the bearing rotation of the polar chart before applying the camera's basis.
    public static S3Frame FromUnfolded(Transform3D pose, double radius)
    {
        Vector3 p = pose.Origin;
        double distance = double.Hypot(p.X, p.Z);
        double bearing = Math.Atan2(p.X, p.Z);
        var frame = S3Frame.OnEquator(distance / radius, bearing).Yaw(bearing);
        frame = frame.Travel(frame.Up, p.Y / radius);
        return Orient(frame, pose.Basis);
    }

    // A compound asset is rigid in its own gnomonic chart, including its offsets.
    // Differentiate the same central projection used to bake its mesh and proxies.
    public static S3Frame FromTangent(S3Frame chart, Transform3D pose, double radius)
    {
        Vector3 v = pose.Origin;
        D4 p = (chart.P + chart.Local(v.X, v.Y, v.Z) / radius).Unit();
        return Orient(chart with { P = p }, pose.Basis);
    }

    private static S3Frame Orient(S3Frame f, Basis basis)
    {
        D4 Axis(Vector3 v) => f.Local(v.X, v.Y, v.Z);
        // Preserve the viewed direction even for nonuniform scale in an editor parent.
        D4 back = Axis(basis.Z).Reject(f.P).Unit();
        D4 right = Axis(basis.X).Reject(f.P).Reject(back).Unit();
        return new(right, D4.Hodge(right, back, f.P), back, f.P);
    }

    public static S3Frame Blend(S3Frame a, S3Frame b, double weight)
    {
        if (weight <= 0) return a;
        if (weight >= 1) return b;
        // Bring both cameras to the same tangent space before interpolating their
        // rotations. Opposite headings are common near a rotated authoring chart.
        double angle = a.P.AngleTo(b.P);
        if (angle > 1e-12)
        {
            D4 towards = b.P.Reject(a.P);
            D4 direction = towards.Length > 1e-12 ? towards.Unit() : a.Back;
            var ray = new ArcRay(a.P, direction);
            a = a.Travel(direction, angle * weight);
            b = b.Travel(-ray.Tangent(angle), angle * (1 - weight));
        }
        Vector3 Local(D4 axis) => new((float)a.Right.Dot(axis), (float)a.Up.Dot(axis), (float)a.Back.Dot(axis));
        var relative = new Basis(Local(b.Right), Local(b.Up), Local(b.Back)).GetRotationQuaternion();
        return Orient(a, new Basis(Quaternion.Identity.Slerp(relative, (float)weight)));
    }
}
