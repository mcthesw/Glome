#nullable enable
using System;

namespace Glome.Core;

public readonly record struct SphericalCap(D4 Center, double CosAlpha)
{
    public static readonly SphericalCap Full = new(D4.Pole, -1);

    public static SphericalCap Around(D4 a, D4 b, D4 c)
    {
        D4 sum = a + b + c;
        if (sum.Length < 1e-12) return Full;
        D4 center = sum.Unit();
        double k = Math.Min(center.Dot(a), Math.Min(center.Dot(b), center.Dot(c)));
        // A cap within a hemisphere contains all normalized positive combinations.
        return k > 0 ? new(center, Math.Max(0, k - 2e-14)) : Full;
    }

    public static SphericalCap Merge(SphericalCap a, SphericalCap b)
    {
        double aa = Math.Acos(a.CosAlpha), ab = Math.Acos(b.CosAlpha);
        double separation = a.Center.AngleTo(b.Center);
        if (aa >= separation + ab) return a;
        if (ab >= separation + aa) return b;
        double radius = (separation + aa + ab) * 0.5;
        D4 tangent = b.Center.Reject(a.Center);
        if (radius >= Math.PI || a.Center.Dot(b.Center) < -1 + 1e-10 || tangent.Length < 1e-12) return Full;
        D4 center = new ArcRay(a.Center, tangent.Unit()).At(radius - aa).Unit();
        return new(center, Math.Max(-1, Math.Cos(radius) - 2e-14));
    }

    public bool Intersects(ArcRay ray, double lo, double hi)
    {
        if (hi < lo) return false;
        double a = Center.Dot(ray.P), b = Center.Dot(ray.D);
        double PeakAt(double t) => a * Math.Cos(t) + b * Math.Sin(t);
        double k = CosAlpha - 2e-14;
        if (PeakAt(lo) >= k || PeakAt(hi) >= k) return true;
        double peak = Math.Atan2(b, a);
        peak += Math.Ceiling((lo - peak) / Math.Tau) * Math.Tau;
        return peak <= hi && Math.Sqrt(a * a + b * b) >= k;
    }
}
