#nullable enable
using System;
using Glome.Core;
using Godot;

namespace Glome;

public enum SpanFitMode { WholeSpans, TrimFill }

/// <summary>A run of rigid +X-facing assets along a short great-circle arc.</summary>
[Tool, GlobalClass]
public partial class S3GeodesicSpan3D : S3Anchor3D
{
    /// <summary>Endpoint in the start pivot's tangent frame. Direction is the initial heading;
    /// magnitude is angular distance in radians (0 to π). The angle survives radius edits.</summary>
    [Export] public Vector3 EndOffset { get; set; } = Vector3.Right;
    /// <summary>Optional local tangent towards the midpoint at an antipodal endpoint.</summary>
    [Export] public Vector3 MidOffset { get; set; }
    /// <summary>Rigid bay authored along +X, with its start at x = 0.</summary>
    [Export] public PackedScene? SegmentScene { get; set; }
    [Export] public PackedScene? EndCapScene { get; set; }
    /// <summary>Optional continuous fill, authored from 0 to SegmentLength on X. Only TrimFill uses it.</summary>
    [Export] public PackedScene? FillScene { get; set; }
    private double _segmentLength = 3;
    [Export(PropertyHint.Range, "0.01,100,0.01,or_greater,suffix:m")]
    public double SegmentLength
    {
        get => _segmentLength;
        set { if (double.IsFinite(value) && value > 0) _segmentLength = value; }
    }
    [Export] public SpanFitMode FitMode { get; set; }

    internal GeodesicSpanLayout Layout(double radius)
    {
        var (frame, pose) = ChartPose(radius);
        if (Mathf.IsZeroApprox(pose.Basis.Determinant())) pose.Basis = Basis.Identity;
        return Layout(ChartCamera.FromTangent(frame, pose, radius), radius, EndOffset, MidOffset);
    }

    internal GeodesicSpanLayout Layout(S3Frame start, double radius, Vector3 end, Vector3 mid) =>
        new(start, radius, end, mid, SegmentLength, FitMode);

    internal PackedScene? Asset(SpanPiece piece) => piece switch
    {
        SpanPiece.Bay => SegmentScene, SpanPiece.Fill => FillScene, _ => EndCapScene
    };
}
