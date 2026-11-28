#nullable enable
using System;

namespace Glome.Core;

// A convex feature supplies a signed point distance and a separating great
// plane. The closest axis point is an endpoint or a stationary point between
// them. The derivative is the supporting normal dotted with the axis tangent.
internal readonly struct CapsuleFeature
{
    private readonly CollisionProxy? _proxy;
    private readonly GeodesicTriangle? _face;
    private readonly CollisionFace _bounds;
    private readonly bool _walkable, _guided;
    internal readonly int Id;
    internal CapsuleFeature(CollisionFace face, int id) =>
        (_face, _bounds, _walkable, _guided, Id) = (face.Triangle, face, face.Walkable, face.Guided, id);
    internal CapsuleFeature(CollisionProxy proxy, int id) =>
        (_proxy, _walkable, Id) = (proxy, proxy.WalkableTop, id);
    internal static CapsuleFeature Ground => new();
    internal bool IsGround => _proxy == null && _face == null;
    internal bool CanSupport => IsGround || _walkable;

    internal bool MayReach(AxisSegment a, AxisSegment b, double sinRadius)
    {
        if (_proxy != null)
        {
            foreach (var plane in _proxy.Solid.Faces)
                if (Clear(plane, a, b, sinRadius)) return false;
        }
        else
        {
            if (_face != null && (Clear(_bounds.EdgeA, a, b, sinRadius) || Clear(_bounds.EdgeB, a, b, sinRadius) ||
                Clear(_bounds.EdgeC, a, b, sinRadius))) return false;
            var plane = _face?.Plane ?? D4.Up;
            if (Clear(plane, a, b, sinRadius) || Clear(-plane, a, b, sinRadius)) return false;
        }
        return true;
    }

    private static bool Clear(D4 plane, AxisSegment a, AxisSegment b, double limit) =>
        plane.Dot(a.Head) > limit && plane.Dot(a.Base) > limit &&
        plane.Dot(b.Head) > limit && plane.Dot(b.Base) > limit;

    private (double Distance, D4 Plane) Distance(D4 p)
    {
        if (_proxy != null) return _proxy.Solid.Distance(p);
        var face = _face;
        var plane = face?.Plane ?? D4.Up;
        double height = plane.Dot(p);
        if (face == null || (face.Dual0.Dot(p) >= 0 && face.Dual1.Dot(p) >= 0 && face.Dual2.Dot(p) >= 0))
            return (Math.Asin(Math.Min(1, Math.Abs(height))), height < 0 ? -plane : plane);

        // Projection onto the interior failed. Test the three finite edges,
        // including their endpoints. Each edge lies on a great circle.
        static D4 Nearest(D4 a, D4 b, D4 point)
        {
            double ab = a.Dot(b), pa = point.Dot(a), pb = point.Dot(b);
            double left = pa - ab * pb, right = pb - ab * pa;
            if (left <= 0) return b;
            if (right <= 0) return a;
            return (a * left + b * right).Unit();
        }
        var q = Nearest(face.A.V, face.B.V, p);
        var bc = Nearest(face.B.V, face.C.V, p);
        var ca = Nearest(face.C.V, face.A.V, p);
        if (bc.Dot(p) > q.Dot(p)) q = bc;
        if (ca.Dot(p) > q.Dot(p)) q = ca;
        var normal = p.Reject(q);
        return (Math.Atan2(normal.Length, q.Dot(p)), normal.Length < 1e-13 ? plane : normal.Unit());
    }

    internal (D4 Axis, double Distance, D4 Plane) Closest(AxisSegment axis)
    {
        var a = axis.Head;
        var (distance, plane) = Distance(a);
        if (a == axis.Base || plane.Dot(axis.Base.Reject(a)) >= -1e-14) return (a, distance, plane);
        var b = axis.Base;
        (distance, plane) = Distance(b);
        if (plane.Dot(a.Reject(b)) >= -1e-14) return (b, distance, plane);
        D4 point = b;
        // Bisection on the normalized axis chord. A 2 m axis has a residual
        // interval under 2 micrometres after twenty iterations.
        for (int i = 0; i < 20; i++)
        {
            point = ((a + b) * 0.5).Unit();
            (distance, plane) = Distance(point);
            double derivative = plane.Dot(axis.Base.Reject(point));
            if (Math.Abs(derivative) < 1e-15) break;
            if (derivative > 0) b = point;
            else a = point;
        }
        return (point, distance, plane);
    }

    internal CapsuleHit Hit(double fraction, D4 axis, double distance, D4 plane, double penetration)
    {
        D4 normal = plane.Reject(axis).Unit();
        D4 point = new ArcRay(axis, -normal).At(distance);
        D4 walk = default;
        if (IsGround || _proxy != null && _walkable && Math.Abs(_proxy.Solid.Faces[4].Dot(point)) < 2e-7)
            walk = IsGround ? D4.Up : _proxy!.Solid.Faces[4];
        if (_face != null && _walkable)
        {
            walk = _face.A.Normal;
            if (_guided)
                walk = _face.A.Normal * _face.Dual0.Dot(point) + _face.B.Normal * _face.Dual1.Dot(point)
                     + _face.C.Normal * _face.Dual2.Dot(point);
            walk = walk.Reject(point).Unit();
        }
        D2 uv = default;
        if (_face != null && _guided)
        {
            double a = _face.Dual0.Dot(point), b = _face.Dual1.Dot(point), c = _face.Dual2.Dot(point);
            uv = (_face.A.Uv * a + _face.B.Uv * b + _face.C.Uv * c) * (1 / (a + b + c));
        }
        return new(fraction, point, normal, walk, IsGround ? -1 : Id,
            _face?.Surface ?? 0, _guided, Math.Max(0, penetration), uv);
    }
}
