#nullable enable
using System;

namespace Glome.Core;

public enum HitKind { Miss, Ground, Triangle }
public readonly record struct RayHit(HitKind Kind, int Triangle, double T, D2 Uv);

public sealed class BakedGeometry(double radius, GeodesicTriangle[] triangles, CapNode[] nodes,
                                  bool equatorialGroundEnabled = true, Func<int, D2, bool>? covers = null,
                                  D2? groundUvScale = null, D2 groundUvOffset = default, int cutoutGround = -1,
                                  Predicate<D4>? groundMask = null)
{
    private readonly Func<D4, D2, bool>? _coversGround = GroundCoverage(groundMask, cutoutGround, covers);
    private readonly double _traceLimit = equatorialGroundEnabled && groundMask is null && cutoutGround < 0 ? Math.PI : Math.Tau;

    private static Func<D4, D2, bool>? GroundCoverage(Predicate<D4>? mask, int material, Func<int, D2, bool>? coverage)
    {
        if (material < 0 || coverage is null) return mask is null ? null : (point, _) => mask(point);
        return (point, uv) => (mask is null || mask(point)) && coverage(material, uv);
    }

    public const double RayStart = 0.00001;
    public double Radius { get; } = radius;
    public GeodesicTriangle[] Triangles { get; } = triangles;
    public CapNode[] Nodes { get; } = nodes;
    public bool EquatorialGroundEnabled { get; } = equatorialGroundEnabled;

    public RayHit Trace(ArcRay ray, bool bruteForce = false, double lo = RayStart, double? hi = null,
                        int ignoredTriangle = -1, Predicate<GeodesicTriangle>? filter = null, bool includeEquator = true)
    {
        double end = hi ?? _traceLimit;
        var hit = new RayHit(HitKind.Miss, -1, end, default);
        double ground = ArcRay.FirstRoot(ray.P.Y, ray.D.Y, lo);
        if (EquatorialGroundEnabled && includeEquator && double.IsFinite(ground) && ground <= end)
        {
            var scale = groundUvScale ?? new D2(0.25, 0.25);
            for (int crossing = 0; crossing < 2 && ground <= end; crossing++, ground += Math.PI)
            {
                D4 x = ray.At(ground);
                var uv = new D2(x.X * Radius * scale.X, x.Z * Radius * scale.Y) + groundUvOffset;
                if (_coversGround != null && !_coversGround(x, uv)) continue;
                hit = new(HitKind.Ground, -1, ground, uv);
                break;
            }
        }
        void Visit(int index)
        {
            if (index == ignoredTriangle || (filter != null && !filter(Triangles[index]))) return;
            var tri = Triangles[index];
            if (tri.Intersect(ray, lo, hit.T, out double t, out var uv) &&
                ((tri.Traversal & TraversalFlags.Cutout) == 0 || covers == null || covers(tri.Material, uv)) &&
                (t < hit.T || hit.Kind != HitKind.Triangle || index < hit.Triangle))
                hit = new(HitKind.Triangle, index, t, uv);
        }
        if (bruteForce)
        {
            for (int i = 0; i < Triangles.Length; i++) Visit(i);
        }
        else
        {
            for (int i = 0; i < Nodes.Length;)
            {
                var node = Nodes[i];
                if (!node.Cap.Intersects(ray, lo, hit.T)) { i = node.Escape; continue; }
                for (int j = 0; j < node.Count; j++) Visit(node.First + j);
                i++;
            }
        }
        return hit;
    }
}
