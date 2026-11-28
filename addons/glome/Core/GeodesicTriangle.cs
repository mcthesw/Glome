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
        : this(a,b,c,material,surface ?? material,traversal, Tangents(a.V,b.V,c.V) ??
            throw new ArgumentException("Projected triangle has zero area.")) { }

    internal static GeodesicTriangle? TryCreate(in ChartVertex a, in ChartVertex b, in ChartVertex c, int material,
        int surface, TraversalFlags traversal) => Tangents(a.V,b.V,c.V) is { } edges
        ? new(a,b,c,material,surface,traversal,edges) : null;

    private readonly record struct Edges(D4 U, D4 V, double Width, double Offset, double Height);

    private static Edges? Tangents(D4 a, D4 b, D4 c)
    {
        D4 e = (b - a).Reject(a), f = (c - a).Reject(a);
        // QR of the two edges avoids subtracting nearly equal Gram determinants.
        // A CSG sliver can lose its last distinct vertex in the affine transform.
        // Test the projected edges, where both rendering and picking consume them.
        double width = e.Length;
        if (width <= 1e-15) return null;
        D4 u = e / width;
        double offset = f.Dot(u);
        var remainder = f - u * offset;
        double height = remainder.Length;
        if (height <= Math.Max(1e-15, f.Length * 1e-12)) return null;
        var v = remainder / height;
        return new(u, v, width, offset, f.Dot(v));
    }

    private GeodesicTriangle(in ChartVertex a, in ChartVertex b, in ChartVertex c, int material, int surface,
        TraversalFlags traversal, Edges edges)
    {
        (A, B, C, Material, Surface, Traversal) = (a, b, c, material, surface, traversal);
        Plane = D4.Hodge(a.V, edges.U, edges.V).Unit();
        Dual2 = edges.V / edges.Height;
        Dual1 = (edges.U - Dual2 * edges.Offset) / edges.Width;
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
