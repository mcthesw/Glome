#nullable enable
using System;

namespace Glome.Core;

// Intersections of the positive homogeneous cone with the unit 3-sphere.
// All candidate features use ambient unit vectors, including finite edges and rims.
internal sealed class ProxyBoundary
{
    internal readonly D4[] Faces;
    private readonly D4 _pole, _radialX, _radialZ, _axis;
    private readonly bool _round;
    private readonly D4[] _corners;
    private readonly SphericalEllipse _lower, _upper, _side;

    internal ProxyBoundary(D4 pole, D4 x, D4 y, D4 z, D4 half, double extension, bool round)
    {
        _pole = pole;
        _round = round;
        _radialX = x/half.X;
        _radialZ = z/half.Z;
        Faces = [(x-pole*half.X).Unit(), (-x-pole*half.X).Unit(),
                 (z-pole*half.Z).Unit(), (-z-pole*half.Z).Unit(),
                 (y-pole*half.Y).Unit(), (-y-pole*(half.Y+extension)).Unit()];
        // Dual basis of the inverse projective transform; pole.dot(point) = 1.
        static D4 Dual(D4 row, D4 a, D4 b, D4 c)
        {
            D4 v = D4.Hodge(a,b,c);
            return v/v.Dot(row);
        }
        D4 origin = Dual(pole,x,y,z), dx = Dual(x,y,z,pole)*half.X;
        D4 dy = Dual(y,x,z,pole), dz = Dual(z,x,y,pole)*half.Z;
        D4 bottom = origin - dy*(half.Y + extension), top = origin + dy*half.Y;
        _axis = dy.Unit();
        _lower = new(bottom,dx,dz);
        _upper = new(top,dx,dz);
        _side = new(origin.Reject(_axis),dx.Reject(_axis),dz.Reject(_axis));
        _corners = round ? [] : new D4[8];
        for (int i = 0; i < _corners.Length; i++)
            _corners[i] = (((i&4)==0 ? bottom : top) + ((i&2)==0 ? -dx : dx) + ((i&1)==0 ? -dz : dz)).Unit();
    }

    private bool Within(D4 p)
    {
        foreach (D4 f in Faces) if (f.Dot(p) > 2e-14) return false;
        return !_round || double.Hypot(_radialX.Dot(p),_radialZ.Dot(p)) <= _pole.Dot(p) + 2e-14;
    }

    internal (double Angle, D4 Plane) Distance(D4 p)
    {
        bool inside = Within(p);
        double best = double.PositiveInfinity;
        D4 nearest = default, support = default;
        void Candidate(D4 q, D4 plane = default)
        {
            double chord = (q-p).Dot(q-p);
            if (chord < best) { nearest = q; best = chord; support = plane; }
        }
        // A valid projection onto a violated support plane attains its lower bound.
        for (int i = _round ? 4 : 0; i < Faces.Length; i++)
        {
            D4 f = Faces[i];
            double signed = f.Dot(p);
            D4 projection = p.Reject(f);
            if (projection.Length < 1e-14) continue;
            D4 q = projection.Unit();
            if (!Within(q)) continue;
            if (signed >= 0) return (Math.Asin(Math.Clamp(signed,0,1)), f);
            Candidate(q,f);
        }
        if (_round)
        {
            // The infinite side is ruled by great circles through the axis.
            // Optimize in its perpendicular section, then clip the generator to the caps.
            D4 u = _side.Nearest(p.Reject(_axis));
            D4 projected = u*p.Dot(u) + _axis*p.Dot(_axis);
            if (p.Dot(u) > 0 && projected.Length > 0)
            {
                D4 q = projected.Unit();
                if (Faces[4].Dot(q) <= 1e-14 && Faces[5].Dot(q) <= 1e-14)
                {
                    D4 sideNormal = (_radialX*_radialX.Dot(q) + _radialZ*_radialZ.Dot(q) - _pole*_pole.Dot(q)).Unit();
                    Candidate(q,sideNormal);
                }
            }
            Candidate(_lower.Nearest(p));
            Candidate(_upper.Nearest(p));
        }
        else
        {
            for (int i = 0; i < 8; i++)
            {
                D4 a = _corners[i];
                Candidate(a);
                for (int bit = 1; bit <= 4; bit *= 2)
                {
                    if ((i & bit) != 0) continue;
                    D4 b = _corners[i | bit], tangent = b.Reject(a).Unit();
                    if (p.Dot(tangent) <= 0 || p.Dot(a.Reject(b)) <= 0) continue;
                    Candidate((a*p.Dot(a) + tangent*p.Dot(tangent)).Unit());
                }
            }
        }
        double angle = nearest.AngleTo(p);
        if (support == default)
        {
            D4 n = p.Reject(nearest);
            support = n.Length > 1e-14 ? n.Unit() * (inside ? -1 : 1) : Faces[4];
        }
        return (inside ? -angle : angle, support);
    }
}
