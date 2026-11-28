#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Glome.Core;

public sealed class S3CollisionWorld
{
    private readonly CollisionProxy[] _proxies;
    private CollisionProxy[] _decks;
    public bool HasElevation => _decks.Length > 0;
    public double Radius { get; }
    public bool EquatorialGroundEnabled { get; }
    public int ProxyCount => _proxies.Length;
    internal int ProxyRevision { get; private set; }

    public S3CollisionWorld(double radius, IEnumerable<CollisionProxy> proxies, bool equatorialGroundEnabled = true)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
        EquatorialGroundEnabled = equatorialGroundEnabled;
        _proxies = proxies.ToArray();
        _decks = Array.FindAll(_proxies, p => p.WalkableTop);
    }

    public CapsuleWorld CreateCapsuleWorld(IEnumerable<CollisionFace>? surfaces = null,
        Predicate<int>? includeProxy = null, Predicate<D4>? coversGround = null) =>
        new(this, _proxies, includeProxy, surfaces, coversGround);

    public double SupportHeight(D4 ground, double ceiling) => SupportHeight(ground, ceiling, out _);

    internal double SupportHeight(D4 ground, double ceiling, out CollisionProxy? support)
    {
        double highest = 0;
        support = null;
        foreach (var deck in _decks)
        {
            if (!deck.TopHeight(ground, out double h) || h > ceiling || h <= highest) continue;
            highest = h;
            support = deck;
        }
        return highest;
    }

    // IDs are insertion slots for the lifetime of this bake, independent of BVH permutation.
    public void ReplaceProxy(int id, CollisionProxy replacement)
    {
        var old = _proxies[id];
        _proxies[id] = replacement;
        ProxyRevision++;
        if (old.WalkableTop || replacement.WalkableTop)
            _decks = Array.FindAll(_proxies, p => p.WalkableTop);
    }

    // First contact of a moving sphere with the authored solid proxies on a short arc.
    public bool SweepSphere(ArcRay ray, double arc, double bodyRadius, out double first, out D4 normal) =>
        Sweep(ray, arc, bodyRadius, 0, out first, out normal);

    private bool Sweep(ArcRay ray, double arc, double bodyRadius, double height, out double first, out D4 normal)
    {
        D4 end = ray.At(arc);
        first = arc;
        normal = default;
        foreach (var proxy in _proxies)
        {
            if (!proxy.Sweep(ray, end, arc, bodyRadius, height, out double t, out D4 n) || t > first) continue;
            first = t;
            normal = n;
        }
        return normal != default;
    }

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
                if (!Sweep(ray, remaining, bodyRadius, standingHeight, out double first, out D4 wall))
                {
                    frame = frame.Travel(d, remaining);
                    onPosition?.Invoke(frame.P);
                    break;
                }
                double advance = Math.Max(0, first - 1e-10 / Radius);
                frame = frame.Travel(d, advance);
                onPosition?.Invoke(frame.P);
                D4 normalAtContact = wall;
                wall = wall.Reject(frame.P);
                if (standingHeight > 0) wall = wall.Reject(HasElevation ? D4.Up.Reject(frame.P).Unit() : D4.Up);
                if (wall.Length < 1e-9) break;
                D4 slide = ray.Tangent(advance).Reject(wall.Unit());
                remaining = (remaining - advance) * slide.Length;
                if (remaining > 1e-9)
                {
                    d = slide.Unit();
                    // A tangent great circle bends into a positive-radius offset.
                    // Aim the slide chord at the same supporting small circle;
                    // its interior stays on the clear side. Sweep it normally so
                    // other features can still interrupt the slide.
                    double latitude = bodyRadius / Radius;
                    D4 support = ray.At(first)*Math.Sin(latitude) + normalAtContact*Math.Cos(latitude);
                    D4 side = wall.Unit();
                    double outward = support.Dot(frame.P)*Math.Tan(remaining/2) / support.Dot(side);
                    if (outward > 0 && outward < 1)
                        d = d*Math.Sqrt(1-outward*outward) + side*outward;
                }
            }
        }
        return frame.Orthonormalized();
    }
}
