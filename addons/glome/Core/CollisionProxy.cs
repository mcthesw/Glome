#nullable enable
using System;

namespace Glome.Core;

public enum ProxyShape { Box, Cylinder }

// XYZ are the linear coefficients of each inverse affine row, W is its translation.
// Forward basis lengths and origin distance conservatively bound the chart-space shape.
public readonly record struct ProxyTransform(D4 InverseX, D4 InverseY, D4 InverseZ,
                                             D4 AxisLengths, double OriginDistance);

public sealed class CollisionProxy
{
    private readonly D4 _pole, _x, _y, _z, _half, _inverseScale;
    private readonly bool _round;
    private readonly double _bound, _paddingScale, _heightScale, _radius;

    public CollisionProxy(S3Frame chart, double radius, ProxyTransform transform,
                          D4 halfExtents, ProxyShape shape)
    {
        _radius = radius;
        _pole = chart.P;
        D4 Axis(D4 row) => chart.Local(row.X, row.Y, row.Z) * radius + chart.P * row.W;
        _x = Axis(transform.InverseX);
        _y = Axis(transform.InverseY);
        _z = Axis(transform.InverseZ);
        static double RowLength(D4 row) => (row with { W = 0 }).Length;
        _inverseScale = new(RowLength(transform.InverseX), RowLength(transform.InverseY),
                            RowLength(transform.InverseZ), 0);
        _half = halfExtents;
        _round = shape == ProxyShape.Cylinder;
        _bound = transform.OriginDistance + transform.AxisLengths.Dot(_half);
        _paddingScale = transform.AxisLengths.Dot(_inverseScale);
        _heightScale = transform.AxisLengths.Y * _inverseScale.Y;
    }

    // A landing or a new curvature can place a stationary body inside a proxy.
    // Exit across a supporting great sphere, keeping a walking foot on the equator.
    internal bool Separate(S3Frame frame, double bodyRadius, double height, out S3Frame clear)
    {
        clear = frame;
        double w = _pole.Dot(frame.P);
        if (w <= 0) return false;
        D4 size = _half + _inverseScale * bodyRadius;
        double x = _x.Dot(frame.P) / w, y = _y.Dot(frame.P) / w, z = _z.Dot(frame.P) / w;
        double bottom = size.Y + height * _inverseScale.Y;
        if (y < -bottom || y > size.Y || Math.Abs(x) > size.X || Math.Abs(z) > size.Z) return false;
        double best = double.PositiveInfinity;
        D4 direction = default;
        void Face(D4 plane)
        {
            if (height > 0) plane = plane.Reject(D4.Up);
            D4 tangent = plane.Reject(frame.P);
            if (tangent.Length < 1e-12) return;
            double angle = Math.Atan2(-plane.Dot(frame.P), tangent.Length);
            if (angle < best) { best = angle; direction = tangent.Unit(); }
        }
        if (_round)
        {
            double radial = double.Hypot(x / size.X, z / size.Z);
            if (radial > 1) return false;
            Face(radial < 1e-12 ? _x / size.X - _pole : _x * (x / (size.X * size.X * radial)) + _z * (z / (size.Z * size.Z * radial)) - _pole);
        }
        else
        {
            Face(_x - _pole * size.X); Face(-_x - _pole * size.X);
            Face(_z - _pole * size.Z); Face(-_z - _pole * size.Z);
        }
        Face(_y - _pole * size.Y); Face(-_y - _pole * bottom);
        if (direction == default) return false;
        clear = frame.Travel(direction, Math.Max(0, best) + 0.0005 / _radius).Orthonormalized();
        return true;
    }

    internal bool Sweep(ArcRay ray, D4 end, double arc, double bodyRadius, double height, out double time, out D4 normal)
    {
        time = 0; normal = default;
        double w0 = _pole.Dot(ray.P), w1 = _pole.Dot(end);
        // The normalized positive chord traces the great circle. Its length is at least
        // cos(arc/2), so even the nearest possible proxy point has this lower bound on w.
        double metres = _bound + bodyRadius * _paddingScale + height * _heightScale;
        double minW = Math.Cos(arc * 0.5) * _radius / double.Hypot(_radius, metres);
        if (Math.Max(w0, w1) < minW - 1e-12) return false;
        double clip = minW * 0.5;
        double s0 = w0 < clip ? (clip - w0) / (w1 - w0) : 0;
        double s1 = w1 < clip ? (clip - w0) / (w1 - w0) : 1;
        D4 chord = end - ray.P;
        D4 p = ray.P + chord * s0, q = ray.P + chord * s1;
        double wa = w0 + (w1 - w0) * s0, wb = w0 + (w1 - w0) * s1;

        D4 Local(D4 p, double w) => new(_x.Dot(p) / w, _y.Dot(p) / w, _z.Dot(p) / w, 0);
        D4 a = Local(p, wa), delta = Local(q, wb) - a;
        D4 pad = _inverseScale * bodyRadius, size = _half + pad;
        double enter = double.NegativeInfinity, leave = 1;
        D4 face = default;
        void Slab(int axis, D4 positive, double low, double high)
        {
            double v = delta[axis], origin = a[axis];
            if (Math.Abs(v) < 1e-12)
            {
                if (origin < low || origin > high) leave = double.NegativeInfinity;
                return;
            }
            double t0 = (low - origin) / v, t1 = (high - origin) / v;
            if (v < 0) (t0, t1) = (t1, t0);
            if (t0 > enter) { enter = t0; face = v > 0 ? -positive : positive; }
            leave = Math.Min(leave, t1);
        }
        Slab(1, D4.Up, -size.Y - height * _inverseScale.Y, size.Y);
        if (_round)
        {
            // The projected arc is a line. Clip it analytically against the elliptical wall.
            double dx = delta.X / size.X, dz = delta.Z / size.Z, ax = a.X / size.X, az = a.Z / size.Z;
            double aa = dx * dx + dz * dz, bb = ax * dx + az * dz, cc = ax * ax + az * az - 1;
            if (aa < 1e-20)
            {
                if (cc > 0) return false;
            }
            else
            {
                double disc = bb * bb - aa * cc;
                if (disc <= 0) return false;
                double root = Math.Sqrt(disc);
                // Stable quadratic roots also work for rays entering from the chart horizon.
                double numerator = -bb - Math.CopySign(root, bb);
                double near = numerator / aa, far = cc / numerator;
                if (near > far) (near, far) = (far, near);
                if (near > enter)
                {
                    enter = near;
                    face = new((ax + near * dx) / size.X, 0, (az + near * dz) / size.Z, 0);
                }
                leave = Math.Min(leave, far);
            }
        }
        else
        {
            Slab(0, D4.Right, -size.X, size.X);
            Slab(2, D4.Back, -size.Z, size.Z);
        }
        if (enter > leave || enter < -1e-9 || enter > 1) return false;
        double s = Math.Max(enter, 0);
        double fraction = s0 + (s1 - s0) * s * wa / ((1 - s) * wb + s * wa);
        time = Math.Atan2(fraction * Math.Sin(arc), 1 - fraction + fraction * Math.Cos(arc));
        D4 point = a + delta * s;
        normal = (_x * face.X + _y * face.Y + _z * face.Z - _pole * point.Dot(face)).Unit();
        return true;
    }
}
