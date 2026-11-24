#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Glome.Core;

public sealed class S3CollisionWorld
{
    private readonly CollisionProxy[] _proxies;
    public double Radius { get; }
    public bool EquatorialGroundEnabled { get; }
    public int ProxyCount => _proxies.Length;

    public S3CollisionWorld(double radius, IEnumerable<CollisionProxy> proxies, bool equatorialGroundEnabled = true)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
        EquatorialGroundEnabled = equatorialGroundEnabled;
        _proxies = proxies.ToArray();
    }

    // IDs are insertion slots for the lifetime of this bake, independent of BVH permutation.
    public void ReplaceProxy(int id, CollisionProxy replacement) => _proxies[id] = replacement;

    public S3Frame ClearPlacement(S3Frame pose, double bodyRadius, double height)
    {
        if (!EquatorialGroundEnabled) height = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            bool changed = false;
            foreach (var proxy in _proxies)
                if (proxy.Separate(pose, bodyRadius, height, out var clear)) { pose = clear; changed = true; }
            if (!changed) break;
        }
        return pose;
    }

    public S3Frame Move(S3Frame frame, D4 direction, double metres, double bodyRadius, double standingHeight,
        Action<D4>? onPosition = null)
    {
        if (!EquatorialGroundEnabled) standingHeight = 0;
        double distance = metres / Radius;
        if (distance <= 1e-12) return frame;
        // Split long movements into arcs shorter than a hemisphere, never into point samples.
        int steps = Math.Max(1, (int)Math.Ceiling(distance / 0.18));
        double x = direction.Dot(frame.Right), y = direction.Dot(frame.Up), z = direction.Dot(frame.Back);
        for (int i = 0; i < steps; i++)
        {
            D4 d = frame.Local(x, y, z).Unit();
            double remaining = distance / steps;
            for (int contact = 0; contact < 4 && remaining > 1e-9; contact++)
            {
                var ray = new ArcRay(frame.P, d);
                D4 end = ray.At(remaining);
                double first = remaining;
                D4 wall = default;
                foreach (var proxy in _proxies)
                {
                    if (!proxy.Sweep(ray, end, remaining, bodyRadius, standingHeight, out double t, out D4 n) || t > first) continue;
                    first = t; wall = n;
                }
                if (wall == default)
                {
                    frame = frame.Travel(d, remaining);
                    onPosition?.Invoke(frame.P);
                    break;
                }
                double advance = Math.Max(0, first - 0.0005 / Radius);
                frame = frame.Travel(d, advance);
                onPosition?.Invoke(frame.P);
                wall = wall.Reject(frame.P);
                if (standingHeight > 0) wall = wall.Reject(D4.Up);
                if (wall.Length < 1e-9) break;
                D4 slide = ray.Tangent(advance).Reject(wall.Unit());
                remaining = (remaining - advance) * slide.Length;
                if (remaining > 1e-9) d = slide.Unit();
            }
        }
        return frame.Orthonormalized();
    }
}
