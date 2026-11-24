#nullable enable
using System;

namespace Glome.Core;

public readonly record struct S3Frame(D4 Right, D4 Up, D4 Back, D4 P)
{
    public static readonly S3Frame Identity = new(D4.Right, D4.Up, D4.Back, D4.Pole);
    public D4 Local(double x, double y, double z) => Right * x + Up * y + Back * z;

    public static D4 ExpMap(D4 p, D4 tangent)
    {
        double t = tangent.Length;
        return t < 1e-15 ? p : new ArcRay(p, tangent / t).At(t);
    }

    public static D4 LogMap(D4 p, D4 q)
    {
        D4 tangent = q.Reject(p);
        double sin = tangent.Length, cos = p.Dot(q);
        if (sin > 1e-14) return tangent * (Math.Atan2(sin, cos) / sin);
        if (cos < 0) throw new ArgumentException("The antipode has no unique logarithm.", nameof(q));
        return tangent;
    }

    // Ambient rotation in span(P,d) parallel-transports every tangent column.
    public S3Frame Travel(D4 direction, double angle)
    {
        D4 unused = default;
        return Travel(direction, angle, ref unused);
    }

    public S3Frame Travel(D4 direction, double angle, ref D4 needle)
    {
        var ray = new ArcRay(P, direction);
        D4 end = ray.At(angle), delta = ray.Tangent(angle) - direction;
        needle += delta * needle.Dot(direction);
        return new(Right + delta * Right.Dot(direction), Up + delta * Up.Dot(direction),
                   Back + delta * Back.Dot(direction), end);
    }

    public S3Frame Yaw(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return this with { Right = Right * c + Back * s, Back = Back * c - Right * s };
    }

    public S3Frame Pitch(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return this with { Up = Up * c + Back * s, Back = Back * c - Up * s };
    }

    public S3Frame Roll(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return this with { Right = Right * c + Up * s, Up = Up * c - Right * s };
    }

    public S3Frame Orthonormalized()
    {
        D4 p = P.Unit(), right = Right.Reject(p).Unit();
        D4 up = Up.Reject(p).Reject(right).Unit();
        return new(right, up, D4.Hodge(right, up, p) * -1, p);
    }

    // Bearing zero travels toward +Z. The origin and antipode presets are exact.
    public static S3Frame OnEquator(double polar, double bearing)
    {
        var (s, c) = Math.SinCos(bearing);
        D4 back = D4.Right * s + D4.Back * c, right = D4.Right * c - D4.Back * s;
        if (polar == Math.PI) return new(right, D4.Up, -back, -D4.Pole);
        return new S3Frame(right, D4.Up, back, D4.Pole).Travel(back, polar);
    }
}
