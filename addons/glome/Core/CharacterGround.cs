#nullable enable
using System;

namespace Glome.Core;

// All distances in metres. Movement trials return a value. Only the caller
// commits a pose, so failure in any of the three step sweeps is atomic.
public sealed class CharacterGround(CapsuleWorld world, S3Capsule shape, double perchRadius, double maxStep)
{
    private const double Skin = 0.00012;
    private readonly double _axisSagitta = shape.Radius * (1 - Math.Cos(shape.AxisLength / world.Radius));
    public S3Capsule Shape => shape;
    public double PerchRadius => perchRadius;
    public CapsuleWorld World => world;

    public S3Frame Translate(S3Frame frame, D4 delta)
    {
        double metres = delta.Length;
        return metres < 1e-12 ? frame : frame.Travel(delta / metres, metres / world.Radius);
    }

    public bool SweepTo(S3Frame from, S3Frame target, out S3Frame reached, out CapsuleHit hit)
    {
        if ((from.P - target.P).Length < 1e-14 && (from.Up - target.Up).Length < 1e-13)
        {
            hit = default;
            reached = target;
            return false;
        }
        if (!world.SweepCapsule(shape, from, target, out hit))
        {
            reached = target;
            return false;
        }
        if (hit.Penetration > Skin / 4 && world.ResolveOverlap(shape, from, out var clear))
        {
            var correction = S3Frame.LogMap(from.P, clear.P);
            target = target.At(S3Frame.ExpMap(target.P, correction.Reject(target.P)));
            from = clear;
            if (!world.SweepCapsule(shape, from, target, out hit))
            {
                reached = target;
                return false;
            }
        }
        double fraction = hit.Fraction;
        double distance = from.P.AngleTo(target.P) * world.Radius;
        double turn = from.At(target.P).Up.AngleTo(target.Up);
        double span = distance + turn * shape.AxisLength;
        fraction = Math.Max(0, fraction - Skin / Math.Max(Skin, span));
        var point = (from.P * (1 - fraction) + target.P * fraction).Unit();
        var moved = from.At(point);
        reached = turn < 1e-12 ? moved : moved.AlignUp(target.At(point).Up, turn * fraction);
        return true;
    }

    public S3Frame Slide(S3Frame start, D4 remaining, CapsuleHit first, out CapsuleHit last)
    {
        var pose = start;
        D4 previous = default;
        last = first;
        for (int pass = 0; pass < 4 && remaining.Length > 1e-8; pass++)
        {
            var normal = last.Normal.Reject(pose.P).Unit();
            var slide = remaining - normal * Math.Min(0, remaining.Dot(normal));
            if (previous != default && slide.Dot(previous) < 0)
            {
                var crease = D4.Hodge(pose.P, normal, previous);
                if (crease.Length > 1e-8) slide = crease * (slide.Dot(crease) / crease.Dot(crease));
            }
            if (slide.Length < 1e-9) break;
            var target = Translate(pose, slide + normal * Math.Min(Skin, slide.Length * 0.02));
            if (!SweepTo(pose, target, out pose, out var hit)) break;
            bool sideContact = !hit.Walkable || hit.Normal.Dot(hit.WalkableNormal) < 0.5;
            if (sideContact && hit.Fraction < 1e-6 && normal.Dot(hit.Normal) > 0.9)
            {
                // A middle-of-spine contact on a finite edge can stall even
                // a tangential gravity impulse. The endpoint clearance differs
                // from that at the contact by at most the spherical sagitta.
                var plane = hit.Normal.Reject(hit.Point).Unit();
                var axis = AxisSegment.At(target, shape.AxisLength / world.Radius);
                double gap = world.Radius * Math.Asin(Math.Clamp(Math.Min(plane.Dot(axis.Head), plane.Dot(axis.Base)), -1, 1));
                double lift = Math.Clamp(shape.Radius + Skin - gap, Skin, Skin + _axisSagitta);
                target = Translate(target, plane.Reject(target.P).Unit() * lift);
                if (!SweepTo(pose, target, out pose, out hit)) break;
            }
            remaining = S3Frame.LogMap(pose.P, target.P) * world.Radius;
            previous = normal;
            last = hit;
        }
        return pose;
    }

    public bool Settle(S3Frame pose, double down, out S3Frame standing, out CapsuleHit floor)
    {
        standing = pose;
        var sole = shape.Sole(pose, world.Radius);
        var probe = sole.Travel(sole.Up, 0.002 / world.Radius);
        if (!world.FindFloor(probe, down + 0.002, perchRadius, out floor)) return false;
        var target = pose.Travel(-pose.Up, down / world.Radius);
        if (!SweepTo(pose, target, out var rest, out var hit) || hit.Normal.Dot(pose.Up) < 0.02)
            return false;
        standing = rest;
        return true;
    }

    public bool StepUp(S3Frame origin, D4 delta, out S3Frame standing, out CapsuleHit floor)
    {
        standing = origin;
        floor = default;
        var raised = origin.Travel(origin.Up, maxStep / world.Radius);
        if (world.SweepCapsule(shape, origin, raised, out _)) return false;
        D4 forward = raised.Local(delta.Dot(origin.Right), 0, delta.Dot(origin.Back));
        var across = Translate(raised, forward);
        if (world.SweepCapsule(shape, raised, across, out _)) return false;
        if (!Settle(across, maxStep * 2 + Skin, out var trial, out var support)) return false;
        standing = trial;
        floor = support;
        return true;
    }

    // A mostly tangential stride keeps its along-edge component. Crossing
    // deliberately uses the complete displacement and may enter free fall.
    public static D4 GetLedgeMove(D4 delta, D4 outward, double deliberateCosine)
    {
        double away = delta.Dot(outward);
        return away > 0 && away < deliberateCosine * delta.Length ? delta - outward * away : delta;
    }

    public bool Walk(S3Frame origin, D4 delta, CapsuleHit previous, double deliberateCosine,
        out S3Frame moved, out CapsuleHit floor)
    {
        var sole = shape.Sole(origin, world.Radius);
        var offEdge = -previous.Point.Reject(sole.P).Reject(sole.Up);
        if (offEdge.Length * world.Radius > 0.025)
            delta = GetLedgeMove(delta, offEdge.Unit().Reject(origin.P).Unit(), deliberateCosine);
        var target = Translate(origin, delta);
        if (SweepTo(origin, target, out moved, out var wall))
        {
            if (StepUp(origin, delta, out var step, out floor))
            {
                moved = step;
                return true;
            }
            D4 normal = wall.Normal.Reject(moved.P).Reject(moved.Up);
            if (normal.Length > 1e-9)
            {
                var slide = delta.Reject(normal.Unit()).Reject(moved.P) * (1 - wall.Fraction);
                SweepTo(moved, Translate(moved, slide), out moved, out _);
            }
        }
        return Settle(moved, maxStep + Skin, out moved, out floor);
    }
}
