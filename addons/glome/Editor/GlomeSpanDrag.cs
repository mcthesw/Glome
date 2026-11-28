#nullable enable
#if TOOLS
using System;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal enum SpanHandle { Start, End, Midpoint }

// Transient endpoint state. The scene is touched only by the release action.
internal sealed class GlomeSpanDrag
{
    internal readonly S3GeodesicSpan3D Span;
    internal readonly SpanHandle Handle;
    internal readonly D4 Pivot;
    internal readonly GizmoPlane Plane;
    private readonly double _radius;
    private readonly GeodesicSpanLayout _initial;
    internal Vector2 Last, Metres;
    internal Vector3 End, Mid;
    internal S3Frame? Spin;
    internal GeodesicSpanLayout Preview { get; private set; }

    internal GlomeSpanDrag(S3GeodesicSpan3D span, SpanHandle handle, S3Frame eye, Vector2 mouse, double radius)
    {
        (Span, Handle, Last, _radius) = (span, handle, mouse, radius);
        (End, Mid) = (span.EndOffset, span.MidOffset);
        _initial = Preview = span.Layout(radius);
        Pivot = handle switch
        {
            SpanHandle.Start => _initial.Start.P,
            SpanHandle.Midpoint => _initial.At(_initial.Length / 2).P,
            _ => _initial.End
        };
        // Face the camera at the handle itself, including endpoints across a chart horizon.
        D4 normal = eye.P.Reject(Pivot).Unit();
        D4 right = eye.Right.Reject(Pivot).Reject(normal).Unit();
        D4 up = eye.Up.Reject(Pivot).Reject(normal).Reject(right).Unit();
        Plane = new(right, up, normal);
    }

    internal void Move(bool snap)
    {
        var start = _initial.Start;
        (Spin, End, Mid) = (null, Span.EndOffset, Span.MidOffset);
        double move = GlomeAuthoring.ArcStep(Metres.Length(), snap);
        if (move > 1e-9)
        {
            D4 axis = (Plane.U * Metres.X + Plane.V * Metres.Y).Unit();
            D4 point = new ArcRay(Pivot, axis).At(move / _radius);
            if (Handle == SpanHandle.Start)
            {
                start = start.Travel(axis, move / _radius);
                Spin = GlomeAuthoring.Between(_initial.Start, start);
                End = Offset(start, _initial.End, _initial.At(_initial.Length / 2).P);
            }
            else if (Handle == SpanHandle.End)
                End = Offset(start, point, _initial.At(_initial.Length / 2).P);
            else
            {
                var heading = Offset(start, point, _initial.At(_initial.Length / 2).P).Normalized();
                if (!heading.IsZeroApprox())
                {
                    Mid = heading * (float)(_initial.Angle / 2);
                    End = heading * (float)_initial.Angle;
                }
            }
            if (Handle != SpanHandle.Midpoint && End.Length() >= Math.PI - 4e-7)
                Mid = End.Normalized() * (Mathf.Pi / 2);
        }
        Preview = Span.Layout(start, _radius, End, Mid);
    }

    private static Vector3 Offset(S3Frame start, D4 point, D4 mid)
    {
        double angle = start.P.AngleTo(point);
        if (angle < 1e-12) return Vector3.Zero;
        var tangent = point.Reject(start.P);
        if (tangent.Length < 1e-12) tangent = mid.Reject(start.P);
        var dir = tangent.Length < 1e-12 ? start.Right : tangent.Unit();
        return new((float)(dir.Dot(start.Right) * angle), (float)(dir.Dot(start.Up) * angle),
            (float)(dir.Dot(start.Back) * angle));
    }
}
#endif
