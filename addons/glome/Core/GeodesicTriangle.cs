#nullable enable
using System;

namespace Glome.Core;

public readonly record struct ChartVertex(D4 V, double K, D2 Uv, D4 Normal)
{
    public static ChartVertex Project(S3Frame chart, double radius, D4 offset, D4 normal, D2 uv)
    {
        D4 position = S3ChartMath.CentralProjection(chart, radius, offset, out double k);
        // Pull the authored Euclidean normal through the gnomonic projection.
        D4 n = (normal - chart.P * (offset.Dot(normal) / radius)).Unit();
        return new(position, k, uv, n);
    }
}

[Flags]
public enum TraversalFlags { CullBack = 0, CullFront = 1, TwoSided = 2, CullMask = 3, Cutout = 4 }

public sealed class GeodesicTriangle
{
    public readonly ChartVertex A, B, C;
    public readonly D4 Plane, Dual0, Dual1, Dual2;
    public readonly int Material;
    public readonly int Surface;
    public readonly TraversalFlags Traversal;
    public readonly SphericalCap Cap;

    public GeodesicTriangle(ChartVertex a, ChartVertex b, ChartVertex c, int material, int? surface = null,
                            TraversalFlags traversal = TraversalFlags.TwoSided)
    {
        (A, B, C, Material) = (a, b, c, material);
        Surface = surface ?? material;
        Traversal = traversal;
        D4 e = (b.V - a.V).Reject(a.V), f = (c.V - a.V).Reject(a.V);
        // QR of the two edges avoids subtracting nearly equal Gram determinants.
        D4 u = e.Unit(), remainder = f.Reject(u);
        D4 v = remainder.Unit();
        Plane = D4.Hodge(a.V, u, v).Unit();
        Dual2 = v / f.Dot(v);
        Dual1 = (u - Dual2 * f.Dot(u)) / e.Length;
        Dual0 = a.V - Dual1 * a.V.Dot(b.V) - Dual2 * a.V.Dot(c.V);
        Cap = SphericalCap.Around(a.V, b.V, c.V);
    }

    public bool Intersect(ArcRay ray, double lo, double hi, out double angle, out D2 uv)
    {
        // The two antipodal crossings exhaust the intersection, even for an unbounded query.
        double first = ArcRay.FirstRoot(Plane.Dot(ray.P), Plane.Dot(ray.D), lo);
        for (int halfTurn = 0; halfTurn < 2; halfTurn++)
        {
            double t = first + halfTurn * Math.PI;
            if (!double.IsFinite(t) || t > hi) break;
            var cull = Traversal & TraversalFlags.CullMask;
            if (cull != TraversalFlags.TwoSided && (Plane.Dot(ray.Tangent(t)) >= 0) == (cull == TraversalFlags.CullBack)) continue;
            D4 x = ray.At(t);
            double b0 = Dual0.Dot(x), b1 = Dual1.Dot(x), b2 = Dual2.Dot(x);
            if (Math.Min(b0, Math.Min(b1, b2)) < -1e-10) continue;
            double l0 = b0 / A.K, l1 = b1 / B.K, l2 = b2 / C.K;
            uv = (A.Uv * l0 + B.Uv * l1 + C.Uv * l2) * (1 / (l0 + l1 + l2));
            angle = t;
            return true;
        }
        angle = 0;
        uv = default;
        return false;
    }
}
