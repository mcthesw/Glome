#nullable enable
using System;

namespace Glome.Core;

public static class S3ChartMath
{
    public static D4 CentralProjection(S3Frame chart, double radius, D4 offset, out double scale)
    {
        D4 homogeneous = chart.P + offset / radius;
        scale = homogeneous.Length;
        return homogeneous / scale;
    }

    public static D4 Apply(S3Frame frame, D4 q) => frame.Local(q.X, q.Y, q.Z) + frame.P * q.W;

    public static S3Frame Compose(S3Frame a, S3Frame b) =>
        new(Apply(a, b.Right), Apply(a, b.Up), Apply(a, b.Back), Apply(a, b.P));

    public static S3Frame Transpose(S3Frame f)
    {
        D4 Row(int i) => new(f.Right[i], f.Up[i], f.Back[i], f.P[i]);
        return new(Row(0), Row(1), Row(2), Row(3));
    }

    public static bool Project(S3Frame center, D4 point, double radius, out D4 metres)
    {
        metres = default;
        if (center.P.AngleTo(point) >= Math.PI - 0.015) return false;
        var t = S3Frame.LogMap(center.P, point) * radius;
        metres = new(t.Dot(center.Right), t.Dot(center.Up), t.Dot(center.Back), 0);
        return true;
    }

    public static D4 Lift(S3Frame center, D4 metres, double radius) =>
        S3Frame.ExpMap(center.P, center.Local(metres.X, metres.Y, metres.Z) / radius);

    private static D4 Product(D4 a, D4 b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
        a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static D4 Conjugate(D4 q) => new(-q.X, -q.Y, -q.Z, q.W);

    public static S3Frame Decode(double[] pair)
    {
        D4 left = new D4(pair[0], pair[1], pair[2], pair[3]).Unit();
        D4 inverse = Conjugate(new D4(pair[4], pair[5], pair[6], pair[7]).Unit());
        D4 Rotate(D4 v) => Product(Product(left, v), inverse);
        return new(Rotate(D4.Right), Rotate(D4.Up), Rotate(D4.Back), Rotate(D4.Pole));
    }

    public static double[] Encode(S3Frame frame)
    {
        // Removing the image of quaternion one leaves conjugation by the right factor.
        D4 Column(D4 v) => Product(Conjugate(frame.P), v);
        D4 x = Column(frame.Right), y = Column(frame.Up), z = Column(frame.Back);
        D4[] candidates = [
            new(1+x.X-y.Y-z.Z, x.Y+y.X, x.Z+z.X, y.Z-z.Y),
            new(x.Y+y.X, 1-x.X+y.Y-z.Z, y.Z+z.Y, z.X-x.Z),
            new(x.Z+z.X, y.Z+z.Y, 1-x.X-y.Y+z.Z, x.Y-y.X),
            new(y.Z-z.Y, z.X-x.Z, x.Y-y.X, 1+x.X+y.Y+z.Z)];
        D4 best = candidates[0];
        foreach (var q in candidates) if (q.Dot(q) > best.Dot(best)) best = q;
        D4 r = best.Unit(), l = Product(frame.P, r);
        return [l.X, l.Y, l.Z, l.W, r.X, r.Y, r.Z, r.W];
    }
}
