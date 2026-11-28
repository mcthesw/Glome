#nullable enable
using System;
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome;

internal enum SpanPiece { Bay, Fill, EndCap }
internal readonly record struct SpanPlacement(SpanPiece Piece, S3Frame Frame, Transform3D Pose)
{
    internal CollisionShapeAdapter.Input Place(CollisionShapeAdapter.Input local) =>
        local with { Chart = Frame, Pose = Pose * local.Pose };
}

internal readonly struct GeodesicSpanLayout
{
    // EndOffset uses Godot's single-precision Vector3. Resolve float(π) and
    // the rounding of an arbitrary unit vector * π to the exact conjugate point.
    private const double EndpointPrecision = 4e-7;
    internal S3Frame Start { get; }
    private readonly S3Frame _aligned;
    private readonly double _radius, _step;
    private readonly SpanFitMode _fit;
    internal double Angle { get; }
    internal double Length => Angle * _radius;
    internal int Bays { get; }
    internal bool ShowMidpoint => Angle > Math.PI - .02;
    internal D4 End => At(Length).P;

    internal GeodesicSpanLayout(S3Frame start, double radius, Vector3 end, Vector3 mid, double step, SpanFitMode fit)
    {
        Start = start;
        (_radius, _step, _fit) = (radius, step, fit);
        var offset = end.IsFinite() ? start.Local(end.X, end.Y, end.Z) : default;
        double angle = offset.Length;
        Angle = angle >= Math.PI - EndpointPrecision ? Math.PI : angle;
        var heading = angle > 1e-12 ? offset / angle : start.Right;
        if (Angle == Math.PI && mid.IsFinite() && !mid.IsZeroApprox())
            heading = start.Local(mid.X, mid.Y, mid.Z).Unit();
        // Orient a bay's longitudinal axis along the run and retain local up
        // wherever possible. At a vertical run, local back fixes the roll.
        var up = start.Up.Reject(heading);
        if (up.Length < 1e-8) up = start.Back.Reject(heading);
        up = up.Unit();
        _aligned = new(heading, up, -D4.Hodge(heading, up, start.P), start.P);
        double count = Angle * radius / step;
        double integer = Math.Round(count);
        Bays = checked((int)(Math.Abs(count - integer) <= Math.Min(1e-5, count * 1e-7) ? integer : Math.Floor(count)));
    }

    // Every frame is evaluated from the original pivot, never from the previous bay.
    internal S3Frame At(double metres) => _aligned.Travel(_aligned.Right, metres / _radius);

    internal IEnumerable<SpanPlacement> Placements()
    {
        double full = Bays * _step;
        for (int i = 0; i < Bays; i++)
            yield return new(SpanPiece.Bay, At(i * _step), Transform3D.Identity);
        if (_fit == SpanFitMode.TrimFill && Length > full + _step * 1e-7)
        {
            // Gnomonic x = R tan(theta). A chart centered on a fill piece makes
            // BOTH its ends land on the arc, even past the start chart's horizon.
            // Split an exceptionally long fill so neither end reaches its chart horizon.
            int pieces = (int)Math.Ceiling((Length - full) / (_radius * Math.PI / 2));
            double piece = (Length - full) / pieces;
            float half = (float)(_radius * Math.Tan(piece / (2 * _radius)));
            var pose = new Transform3D(Basis.FromScale(new((float)(2 * half / _step), 1, 1)), Vector3.Left * half);
            for (int i = 0; i < pieces; i++)
                yield return new(SpanPiece.Fill, At(full + (i + .5) * piece), pose);
        }
        yield return new(SpanPiece.EndCap, At(_fit == SpanFitMode.TrimFill ? Length : full), Transform3D.Identity);
    }
}
