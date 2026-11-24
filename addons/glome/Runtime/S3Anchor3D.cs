#nullable enable
using System;
using Godot;
using Glome.Core;

namespace Glome;

[Tool, GlobalClass]
public partial class S3Anchor3D : Node3D
{
    public enum PlacementMode { Origin, Antipode, PositiveX, NegativeX, PositiveZ, NegativeZ, SphericalAngles, ArcMetres, PositiveY, NegativeY, ParentChart }

    private Vector3 _offset;
    private double[] _relativeSpin = [];
    [Export] public double[] RelativeSpin
    {
        get => _relativeSpin;
        // Godot's text loader can parse the last decimal place one ULP differently.
        // Canonicalize both native undo values and values read back from PackedScene.
        set => _relativeSpin = value is { Length: 8 }
            ? Array.ConvertAll(value, component => Math.Round(component, 14))
            : [];
    }

    [Export] public PlacementMode ChartPlacement { get; set; } = PlacementMode.ParentChart;
    [Export(PropertyHint.Range, "-180,180,degrees")]
    public double BearingDegrees { get; set; }
    [Export(PropertyHint.Range, "0,180,degrees")]
    public double PolarDegrees { get; set; }
    /// <summary>Angular height off the equatorial ground for angle and arc-length placements.</summary>
    [Export(PropertyHint.Range, "-90,90,degrees")]
    public double ElevationDegrees { get; set; }
    [Export(PropertyHint.Range, "0,110,0.1,or_greater,suffix:m")]
    public double ArcMetres { get; set; }
    /// <summary>Translation in the tangent chart, in metres.</summary>
    [Export] public Vector3 LocalOffset
    {
        get => ChartPlacement == PlacementMode.ParentChart ? Position : _offset;
        set
        {
            _offset = value;
            if (ChartPlacement == PlacementMode.ParentChart) Position = value;
        }
    }

    /// <summary>Extent of a rigid compound in the unfolded 3D view, used when following its camera.</summary>
    [Export(PropertyHint.Range, "0,32,0.5,suffix:m")] public float EditorChartRadius { get; set; }

    private double Elevation => ChartPlacement switch
    {
        PlacementMode.PositiveY => Math.PI / 2,
        PlacementMode.NegativeY => -Math.PI / 2,
        PlacementMode.SphericalAngles or PlacementMode.ArcMetres => double.DegreesToRadians(ElevationDegrees),
        _ => 0
    };

    public S3Frame Frame(double radius) => ChartPose(radius).Frame;

    private S3Frame PlacementFrame(double radius)
    {
        double bearing = double.DegreesToRadians(BearingDegrees);
        S3Frame baseFrame = ChartPlacement switch
        {
            PlacementMode.Origin => S3Frame.Identity,
            PlacementMode.Antipode => S3Frame.OnEquator(Math.PI, 0),
            PlacementMode.PositiveX => new(-D4.Back, D4.Up, -D4.Pole, D4.Right),
            PlacementMode.NegativeX => new(D4.Back, D4.Up, -D4.Pole, -D4.Right),
            PlacementMode.PositiveZ => new(D4.Right, D4.Up, -D4.Pole, D4.Back),
            PlacementMode.NegativeZ => new(-D4.Right, D4.Up, -D4.Pole, -D4.Back),
            PlacementMode.PositiveY or PlacementMode.NegativeY => S3Frame.Identity,
            PlacementMode.SphericalAngles => S3Frame.OnEquator(double.DegreesToRadians(PolarDegrees), bearing),
            _ => S3Frame.OnEquator(ArcMetres / radius, bearing)
        };
        double height = Elevation;
        if (Math.Abs(height) == Math.PI / 2)
        {
            double sign = Math.Sign(height);
            return new(baseFrame.Right, -baseFrame.P * sign, baseFrame.Back, D4.Up * sign);
        }
        return height == 0 ? baseFrame : baseFrame.Travel(D4.Up, height);
    }

    public (S3Frame Frame, Transform3D Pose) ChartPose(double radius)
    {
        var chart = BasePose(radius);
        return RelativeSpin is { Length: 8 } spin
            ? (S3ChartMath.Compose(chart.Frame, S3ChartMath.Decode(spin)), chart.Pose)
            : chart;
    }

    public (S3Frame Frame, Transform3D Pose) BasePose(double radius)
    {
        if (ChartPlacement != PlacementMode.ParentChart) return (PlacementFrame(radius), new(Basis, LocalOffset));
        return GlomeChart.InheritedPose(this, radius);
    }
}
