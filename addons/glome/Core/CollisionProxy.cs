#nullable enable
using System;

namespace Glome.Core;

public enum ProxyShape { Box, Cylinder }

// XYZ are the linear coefficients of each inverse affine row, W is its translation.
// The final two fields retain the affine authoring metadata.
public readonly record struct ProxyTransform(D4 InverseX, D4 InverseY, D4 InverseZ,
                                             D4 AxisLengths, double OriginDistance);

public sealed class CollisionProxy
{
    private readonly D4 _pole, _x, _y, _z, _half;
    private readonly bool _round;
    private readonly double _verticalScale, _radius;

    private readonly ProxyBoundary _solid;
    private sealed record Standing(double Height, ProxyBoundary Boundary);
    private volatile Standing? _standing;

    internal bool WalkableTop { get; }

    public CollisionProxy(S3Frame chart, double radius, ProxyTransform transform,
                          D4 halfExtents, ProxyShape shape, bool walkableTop = false)
        : this(chart, radius, transform, halfExtents, shape, walkableTop, D4.Pole) { }

    // Homogeneous denominator in chart coordinates; changing gnomonic chart
    // changes all four inverse rows, even for a locally affine authored box.
    internal CollisionProxy(S3Frame chart, double radius, ProxyTransform transform,
        D4 halfExtents, ProxyShape shape, bool walkableTop, D4 denominator)
    {
        _radius = radius;
        WalkableTop = walkableTop;
        D4 Axis(D4 row) => chart.Local(row.X, row.Y, row.Z) * radius + chart.P * row.W;
        _pole = Axis(denominator);
        _x = Axis(transform.InverseX);
        _y = Axis(transform.InverseY);
        _z = Axis(transform.InverseZ);
        _verticalScale = _y.Reject(_pole).Length / radius;
        _half = halfExtents;
        _round = shape == ProxyShape.Cylinder;
        _solid = Boundary(0);
    }

    private ProxyBoundary Boundary(double height) => new(_pole, _x, _y, _z, _half, height*_verticalScale, _round);

    private ProxyBoundary ForHeight(double height)
    {
        if (height == 0) return _solid;
        var cached = _standing;
        if (cached?.Height == height) return cached.Boundary;
        var boundary = Boundary(height);
        _standing = new(height,boundary);
        return boundary;
    }

    // Intersect the vertical great circle q cos(h/R) + U sin(h/R) with the
    // homogeneous top plane, then clip the strike against the finite chart box.
    internal bool TopHeight(D4 ground, out double metres, bool clip = true)
    {
        D4 plane = _y - _pole * _half.Y;
        metres = 0;
        if (plane.Y <= 0 || _pole.Dot(ground) <= 0) return false;
        double angle = Math.Atan2(-plane.Dot(ground), plane.Y);
        metres = angle * _radius;
        if (!clip) return true;
        D4 strike = ground * Math.Cos(angle) + D4.Up * Math.Sin(angle);
        double w = _pole.Dot(strike);
        if (_round) return w > 0 && double.Hypot(_x.Dot(strike)/_half.X, _z.Dot(strike)/_half.Z) <= w + 1e-12;
        return w > 0 && Math.Abs(_x.Dot(strike)) <= (_half.X + 1e-6) * w
            && Math.Abs(_z.Dot(strike)) <= (_half.Z + 1e-6) * w;
    }

    private bool CanStepOn(D4 feet) =>
        TopHeight(feet.Reject(D4.Up).Unit(), out double top, clip: false) &&
        top <= _radius * Math.Asin(Math.Clamp(feet.Y, -1, 1)) + S3Elevation.StepUp;

    // Distance to any supporting hemisphere is a lower bound. Along a unit
    // speed arc its signed distance can decrease by at most the arc length.
    private static bool Near(ProxyBoundary boundary, D4 p, double angle)
    {
        double limit = Math.Sin(Math.Min(Math.PI / 2, angle)) + 2e-14;
        foreach (D4 face in boundary.Faces)
            if (face.Dot(p) > limit) return false;
        return true;
    }

    internal bool Separate(S3Frame frame, double bodyRadius, double height, out S3Frame clear)
    {
        clear = frame;
        double rho = bodyRadius/_radius;
        var boundary = ForHeight(height);
        if (!Near(boundary,frame.P,rho) || (height > 0 && WalkableTop && CanStepOn(frame.P))) return false;
        var (distance, plane) = boundary.Distance(frame.P);
        if (distance >= rho - 1e-12) return false;
        double best = double.PositiveInfinity;
        D4 direction = default;
        void ExitAcross(D4 n)
        {
            D4 d = n.Reject(frame.P);
            if (height > 0) d = d.Reject(D4.Up.Reject(frame.P).Unit());
            if (d.Length < 1e-12) return;
            d = d.Unit();
            double a = n.Dot(frame.P), b = n.Dot(d), magnitude = double.Hypot(a,b);
            double offset = Math.Sin(rho);
            if (b <= 0 || magnitude < offset) return;
            double t = Math.Asin(offset/magnitude) - Math.Atan2(a,b);
            if (t >= -1e-12 && t < best) { best = t; direction = d; }
        }
        ExitAcross(plane);
        // A walker's nearest feature can be directly overhead. Choose a
        // reachable horizontal exit if vertical depenetration is constrained.
        if (height > 0)
            foreach (D4 n in boundary.Faces) ExitAcross(n);
        if (direction == default) return false;
        clear = frame.Travel(direction, Math.Max(0,best) + 1e-8/_radius).Orthonormalized();
        return true;
    }

    internal bool Sweep(ArcRay ray, D4 end, double arc, double bodyRadius, double height, out double time, out D4 normal)
    {
        time = 0;
        normal = default;
        double rho = bodyRadius/_radius, offset = Math.Sin(rho);
        var boundary = ForHeight(height);
        if (!Near(boundary,ray.P,rho+arc) || (height > 0 && WalkableTop && CanStepOn(end))) return false;
        // At every exterior point the nearest feature gives an exact supporting
        // plane. No contact can precede the first entry into its offset slab.
        while (time <= arc)
        {
            D4 p = ray.At(time), tangent = ray.Tangent(time);
            var (distance, n) = boundary.Distance(p);
            double a = n.Dot(p), b = n.Dot(tangent);
            // A projectile asks for its next entry. A standing footprint can
            // start slightly embedded after its elevation changes; block inward motion.
            if (height == 0 && time == 0 && distance < rho - 2e-12) return false;
            if (distance <= rho + 2e-12 && (time > 0 || b < -1e-12))
            {
                normal = n.Reject(p).Unit();
                return true;
            }
            double magnitude = double.Hypot(a,b);
            if (magnitude <= offset) return false;
            // Descending root of a cos(t) + b sin(t) = sin(rho).
            double delta = Math.Atan2(b,a) + Math.Acos(Math.Clamp(offset/magnitude,-1,1));
            if (delta <= 0) delta += Math.Tau;
            double next = time + delta;
            if (next == time)
            {
                normal = n.Reject(p).Unit();
                return true;
            }
            time = next;
        }
        return false;
    }
}
