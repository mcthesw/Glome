#nullable enable
using System;

namespace Glome.Core;

// A geodesic segment dilated by a metric ball. Poses refer to the upper
// sphere centre (normally the physical eye), regardless of player look.
public readonly record struct S3Capsule(double Radius, double AxisLength)
{
    public S3Frame Sole(S3Frame eye, double worldRadius) =>
        eye.Travel(-eye.Up, (AxisLength + Radius) / worldRadius);

    public S3Frame Eye(S3Frame sole, double worldRadius) =>
        sole.Travel(sole.Up, (AxisLength + Radius) / worldRadius);
}

public readonly record struct CapsuleHit(double Fraction, D4 Point, D4 Normal,
    D4 WalkableNormal, int SurfaceId, int Region, bool Guided, double Penetration, D2 Coordinates = default)
{
    public bool Walkable => WalkableNormal != default;
}

public readonly record struct CollisionFace(GeodesicTriangle Triangle, bool Walkable, bool Guided = false)
{
    internal D4 EdgeA { get; } = -Triangle.Dual0.Unit();
    internal D4 EdgeB { get; } = -Triangle.Dual1.Unit();
    internal D4 EdgeC { get; } = -Triangle.Dual2.Unit();
}

internal readonly record struct AxisSegment(D4 Head, D4 Base)
{
    internal static AxisSegment At(S3Frame pose, double axisAngle) =>
        new(pose.P, new ArcRay(pose.P, -pose.Up).At(axisAngle));

    // Normalized chords stay in the short geodesic segment and in every
    // supporting hemisphere containing both endpoints.
    internal AxisSegment Toward(AxisSegment end, double t) =>
        new((Head + (end.Head - Head) * t).Unit(), (Base + (end.Base - Base) * t).Unit());
}

public static class FrameTransport
{
    public static S3Frame At(this S3Frame frame, D4 point)
    {
        // Closed form of parallel transport along the short geodesic.
        double denominator = 1 + frame.P.Dot(point);
        if (denominator < 1e-12) throw new ArgumentException("Ambiguous antipodal transport.", nameof(point));
        var correction = (frame.P + point) / denominator;
        return new(frame.Right - correction * frame.Right.Dot(point),
                   frame.Up - correction * frame.Up.Dot(point),
                   frame.Back - correction * frame.Back.Dot(point), point);
    }

    // Rotate in the local three-dimensional tangent space, retaining twist.
    public static S3Frame AlignUp(this S3Frame frame, D4 target, double limit = Math.PI)
    {
        target = target.Reject(frame.P).Unit();
        double cos = Math.Clamp(frame.Up.Dot(target), -1, 1);
        var across = target - frame.Up * cos;
        double sin = across.Length;
        if (sin < 1e-12 && cos > 0) return frame;
        across = sin < 1e-12 ? -frame.Right : across / sin;
        var (s, c) = Math.SinCos(Math.Min(limit, Math.Atan2(sin, cos)));
        var up = frame.Up * c + across * s;
        var delta = across * (c - 1) - frame.Up * s;
        return new(frame.Right + delta * frame.Right.Dot(across), up,
                   frame.Back + delta * frame.Back.Dot(across), frame.P);
    }
}
