#nullable enable
using System;

namespace Glome.Core;

public readonly record struct S3AcousticPath(D4 LocalDirection, double DirectDistance, double ComplementaryDistance)
{
    // At coincidence or the antipode either arrival direction is possible. Use a
    // fixed listener-local tangent so infinitesimal roundoff cannot flip the sound.
    public static S3AcousticPath Resolve(S3Frame listener, D4 source, double radius, D4? localFallback = null)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        D4 tangent = source.Reject(listener.P);
        double sine = tangent.Length, cosine = listener.P.Dot(source);
        if (sine <= 1e-12)
        {
            double distance = cosine < 0 ? Math.PI * radius : 0;
            return new(((localFallback ?? D4.Up) with { W = 0 }).Unit(), distance,
                       cosine < 0 ? distance : Math.Tau * radius);
        }

        D4 direction = tangent / sine;
        var local = new D4(direction.Dot(listener.Right), direction.Dot(listener.Up), direction.Dot(listener.Back), 0);
        double theta = Math.Atan2(sine, cosine);
        return new(local, radius * theta, radius * (Math.Tau - theta));
    }
}
