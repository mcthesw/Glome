#nullable enable
using System;

namespace Glome.Core;

public readonly record struct D2(double X, double Y)
{
    public static D2 operator +(D2 a, D2 b) => new(a.X + b.X, a.Y + b.Y);
    public static D2 operator *(D2 a, double k) => new(a.X * k, a.Y * k);
}

public readonly record struct D4(double X, double Y, double Z, double W)
{
    public static readonly D4 Right = new(1, 0, 0, 0);
    public static readonly D4 Up = new(0, 1, 0, 0);
    public static readonly D4 Back = new(0, 0, 1, 0);
    public static readonly D4 Pole = new(0, 0, 0, 1);

    public double this[int axis] => axis switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        3 => W,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    public static D4 operator +(D4 a, D4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static D4 operator -(D4 a, D4 b) => a + -b;
    public static D4 operator -(D4 a) => new(-a.X, -a.Y, -a.Z, -a.W);
    public static D4 operator *(D4 a, double k) => new(a.X * k, a.Y * k, a.Z * k, a.W * k);
    public static D4 operator /(D4 a, double k) => a * (1 / k);
    public double Dot(D4 b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;
    public double Length => Math.Sqrt(Dot(this));
    public D4 Reject(D4 unit) => this - unit * Dot(unit);
    public D4 Unit()
    {
        double length = Length;
        return length > 1e-15 ? this / length : throw new ArithmeticException("Undefined unit vector.");
    }

    // atan2 retains precision both on very short arcs and next to the antipode.
    public double AngleTo(D4 unit) => Math.Atan2(unit.Reject(this).Length, Dot(unit));

    public static D4 Hodge(D4 a, D4 b, D4 c)
    {
        double xy = a.X * b.Y - a.Y * b.X, xz = a.X * b.Z - a.Z * b.X;
        double xw = a.X * b.W - a.W * b.X, yz = a.Y * b.Z - a.Z * b.Y;
        double yw = a.Y * b.W - a.W * b.Y, zw = a.Z * b.W - a.W * b.Z;
        return new(-yz * c.W + yw * c.Z - zw * c.Y, xz * c.W - xw * c.Z + zw * c.X,
                   -xy * c.W + xw * c.Y - yw * c.X, xy * c.Z - xz * c.Y + yz * c.X);
    }
}

public readonly record struct ArcRay(D4 P, D4 D)
{
    public D4 At(double t) => P * Math.Cos(t) + D * Math.Sin(t);
    public D4 Tangent(double t) => D * Math.Cos(t) - P * Math.Sin(t);

    // Roots of a cos(t) + b sin(t) are separated by pi.
    public static double FirstRoot(double a, double b, double lo)
    {
        if (Math.Abs(a) + Math.Abs(b) < 1e-15) return double.PositiveInfinity;
        double t = Math.Atan2(a, -b);
        return t + Math.Ceiling((lo - t) / Math.PI) * Math.PI;
    }
}
