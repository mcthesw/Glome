#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Glome.Core;

public sealed class CapsuleWorld
{
    private const double Skin = 0.00002;
    private readonly CollisionProxy[] _proxies;
    private readonly int[] _slots;
    private readonly CollisionFace[] _faces;
    private readonly CapNode[] _nodes;
    private readonly double[] _sinCap;
    private readonly S3CollisionWorld _world;
    private readonly Predicate<D4>? _coversGround;
    public double Radius => _world.Radius;
    public int Revision => _world.ProxyRevision;

    internal CapsuleWorld(S3CollisionWorld world, CollisionProxy[] proxies, Predicate<int>? include,
        IEnumerable<CollisionFace>? faces, Predicate<D4>? coversGround)
    {
        _world = world;
        _proxies = proxies; // Gate replacements remain visible to every query.
        _slots = Array.FindAll(Enumerable.Range(0, proxies.Length).ToArray(), include ?? (_ => true));
        _faces = faces?.ToArray() ?? [];
        _nodes = CapBvhBuilder.Build(_faces.Select(f => f.Triangle).ToArray(), _faces);
        _sinCap = Array.ConvertAll(_nodes, n => Math.Sqrt(Math.Max(0, 1 - n.Cap.CosAlpha * n.Cap.CosAlpha)));
        _coversGround = coversGround;
    }

    // Both end spheres sweep along short arcs. Subdivide large changes of
    // orientation about the upper centre so the lower sphere describes the turn.
    public bool SweepCapsule(S3Capsule shape, S3Frame start, S3Frame end, out CapsuleHit hit)
    {
        double turn = start.At(end.P).Up.AngleTo(end.Up);
        int pieces = Math.Max(1, (int)Math.Ceiling(turn / 0.035));
        var previous = start;
        for (int i = 1; i <= pieces; i++)
        {
            double t = (double)i / pieces;
            var position = i == pieces ? end.P : (start.P * (1 - t) + end.P * t).Unit();
            var next = i == pieces ? end : start.At(position).AlignUp(end.At(position).Up, turn * t);
            if (Query(shape, previous, next, false, out hit))
            {
                hit = hit with { Fraction = (i - 1 + hit.Fraction) / pieces };
                return true;
            }
            previous = next;
        }
        hit = default;
        return false;
    }

    public bool ResolveOverlap(S3Capsule shape, S3Frame pose, out S3Frame clear)
    {
        clear = pose;
        for (int pass = 0; pass < 12; pass++)
        {
            if (!Query(shape, clear, clear, false, out var hit) || hit.Penetration <= Skin) return true;
            var normal = hit.Normal.Reject(clear.P).Unit();
            clear = clear.Travel(normal, (hit.Penetration + Skin) / Radius);
        }
        return !Query(shape, clear, clear, false, out _);
    }

    // Query the lower hemisphere with an effective foot radius. The starting
    // support plane stays at the sole when the perch radius is reduced.
    public bool FindFloor(S3Frame sole, double distance, double perchRadius, out CapsuleHit hit)
    {
        var start = sole.Travel(sole.Up, perchRadius / Radius);
        var end = start.Travel(-start.Up, distance / Radius);
        return Query(new(perchRadius, 0), start, end, true, out hit);
    }

    private bool Query(S3Capsule shape, S3Frame start, S3Frame end, bool floorOnly, out CapsuleHit result)
    {
        var a = AxisSegment.At(start, shape.AxisLength / Radius);
        var b = AxisSegment.At(end, shape.AxisLength / Radius);
        var centre = (a.Head + a.Base + b.Head + b.Base).Unit();
        double bound = Math.Max(Math.Max(centre.AngleTo(a.Head), centre.AngleTo(a.Base)),
            Math.Max(centre.AngleTo(b.Head), centre.AngleTo(b.Base))) + (shape.Radius + Skin) / Radius;
        var (sin, cos) = Math.SinCos(Math.Min(Math.PI / 2, bound));
        double rho = shape.Radius / Radius, tolerance = Skin / Radius;
        double offset = Math.Sin(rho), limit = Math.Sin(rho + tolerance);
        bool found = false;
        CapsuleHit best = default;

        void Test(CapsuleFeature obstacle)
        {
            if (floorOnly && !obstacle.CanSupport || !obstacle.MayReach(a, b, limit)) return;
            double t = 0;
            for (int iteration = 0; iteration < 40 && (!found || t <= best.Fraction + 1e-8); iteration++)
            {
                var current = t == 0 ? a : a.Toward(b, t);
                var (point, distance, plane) = obstacle.Closest(current);
                // When both final centres are on the clear side, the whole
                // ruled sweep stays there. This also permits tangential slides.
                double finish = Math.Min(plane.Dot(b.Head), plane.Dot(b.Base));
                if (distance >= rho - tolerance && finish >= offset - tolerance) return;
                if (distance <= rho + tolerance)
                {
                    var hit = obstacle.Hit(t, point, distance, plane, (rho - distance) * Radius);
                    if (obstacle.IsGround && _coversGround != null && !_coversGround(hit.Point)) return;
                    if (floorOnly && (!hit.Walkable || hit.WalkableNormal.Dot(start.Up) < 0.55 ||
                        hit.Normal.Dot(start.Up) < 0.05)) return;
                    if (!found || t < best.Fraction - 1e-8 || t <= best.Fraction + 1e-8 &&
                        (hit.Penetration > best.Penetration || hit.Walkable && !best.Walkable))
                    {
                        found = true;
                        best = hit;
                    }
                    return;
                }
                // Solve the first crossing of this support plane on the chord
                // of each centre. Normalization can only delay a positive offset.
                double Enter(D4 first, D4 last)
                {
                    double lo = plane.Dot(first), hi = plane.Dot(last);
                    if (hi >= offset || lo <= hi) return double.PositiveInfinity;
                    // |lerp(first,last,u)|² = 1 - 2(1-first·last)u(1-u).
                    double bend = 2 * (1 - first.Dot(last));
                    double u = Math.Clamp((lo - offset) / (lo - hi), t, 1);
                    for (int k = 0; k < 3; k++)
                    {
                        double length = Math.Sqrt(1 - bend * u * (1 - u));
                        u = Math.Clamp((lo - offset * length) / (lo - hi), t, 1);
                    }
                    return u;
                }
                // A curved axis can touch a finite edge before either endpoint
                // supplies a tight separating plane. Advance by a distance bound too.
                double speedBound = Math.Max((b.Head - a.Head).Length, (b.Base - a.Base).Length)
                    / Math.Max(0.1, cos);
                t = Math.Max(t + (distance - rho) / Math.Max(1e-14, speedBound),
                    Math.Min(Enter(a.Head, b.Head), Enter(a.Base, b.Base)));
                if (t > 1) return;
                if (!double.IsFinite(t)) return;
            }
        }

        if (_world.EquatorialGroundEnabled) Test(CapsuleFeature.Ground);
        foreach (int id in _slots) Test(new(_proxies[id], id));
        for (int i = 0; i < _nodes.Length;)
        {
            var node = _nodes[i];
            if (node.Cap.CosAlpha > 0 && centre.Dot(node.Cap.Center) < node.Cap.CosAlpha * cos - _sinCap[i] * sin)
            {
                i = node.Escape;
                continue;
            }
            for (int j = node.First; j < node.First + node.Count; j++)
                Test(new(_faces[j], _proxies.Length + j));
            i++;
        }
        result = best;
        return found;
    }
}
