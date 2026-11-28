#nullable enable
using System;
using System.Linq;
using Glome;
using Glome.Core;
using Godot;

namespace Glome.StandaloneTests;

internal static class GeodesicSpanChecks
{
    internal static void Run()
    {
        var space = new S3Space3D { AutoStart = false };
        try
        {
            var chart = new S3Anchor3D { ChartPlacement = S3Anchor3D.PlacementMode.PositiveZ, Rotation = new(0,.42f,0) };
            space.AddChild(chart);
            var span = new S3GeodesicSpan3D { Position = new(2,0,-1), SegmentLength = 3,
                EndOffset = new(1.2f,0,.9f), SegmentScene = GD.Load<PackedScene>("res://SpanBay.tscn"),
                EndCapScene = GD.Load<PackedScene>("res://SpanPier.tscn") };
            chart.AddChild(span);
            foreach (double radius in new[] { 16d, 32d })
            {
                space.Radius = radius;
                var layout = span.Layout(radius);
                var (frame, pose) = span.ChartPose(radius);
                var start = ChartCamera.FromTangent(frame, pose, radius);
                var heading = start.Local(1.2, 0, .9).Unit();
                Require(layout.Start == start && layout.Bays == radius / 2, "inherited start / metric count");
                var bake = GlomeBaker.Bake(space, new GlomeBakeOptions(IncludeOwners: true, CaptureAuthoringSources: true));
                var sources = bake.AuthoringSources!;
                Require(bake.Collision.ProxyCount == 2 * layout.Bays + 1 &&
                    bake.TriangleOwners!.All(n => n == span) && sources.Proxies.All(p => p.Node == span) &&
                    span.GetChildCount(true) == 0, "derived sources and scene ownership");
                int bay = 0;
                foreach (var sample in layout.Placements().Where(p => p.Piece == SpanPiece.Bay))
                {
                    var expected = start.Travel(heading, bay * 3 / radius);
                    Require(expected.P.AngleTo(sample.Frame.P) < 5e-8 && expected.Up.AngleTo(sample.Frame.Up) < 1e-8
                        && sample.Pose == Transform3D.Identity, "transported rigid bay " + bay);
                    var proxy = sources.Proxies[2 * bay + 1].Proxy;
                    var rail = sources.Evaluated.Single(p => p.Frame == sample.Frame && p.Pose.Origin.Y == 1.14f);
                    Require(proxy.Chart == rail.Frame && proxy.Pose.Origin.X == rail.Pose.Origin.X &&
                        proxy.Half.X == 1.5f && rail.Bounds.Size.X == 3 && rail.Pose.Basis == Basis.Identity,
                        "unscaled bay geometry and proxy share a chart");
                    D4 At(double z) => (sample.Frame.P + sample.Frame.Local(1.3,1.14,z)/radius).Unit();
                    var p = At(1.7);
                    var ray = new ArcRay(p, At(0).Reject(p).Unit());
                    var hit = bake.Geometry.Trace(ray, hi: 2/radius);
                    Require(hit.Kind == HitKind.Triangle && sources.SourceForTriangle(hit.Triangle) == span &&
                        bake.Collision.SweepSphere(ray, 2/radius, .01, out double t, out _) &&
                        Math.Abs(t - hit.T) * radius < .02, "ray and sweep along bay " + bay);
                    bay++;
                }
                var cap = layout.Placements().Last();
                Require(cap.Piece == SpanPiece.EndCap && cap.Frame.P.AngleTo(layout.End) < 1e-7, "exact multiple cap");
            }
#if TOOLS
            var initial = span.Layout(space.Radius);
            var eye = initial.Start.Travel(initial.Start.Back, .4);
            foreach (var handle in new[] { Editor.SpanHandle.Start, Editor.SpanHandle.End })
            {
                var drag = new Editor.GlomeSpanDrag(span, handle, eye, Vector2.Zero, space.Radius)
                    { Metres = new Vector2(.4f, .2f) };
                drag.Move(false);
                var preview = drag.Preview;
                bool startMoved = preview.Start.P.AngleTo(initial.Start.P) > .005;
                bool endMoved = preview.End.AngleTo(initial.End) > .005;
                Require(startMoved == (handle == Editor.SpanHandle.Start) && endMoved == (handle == Editor.SpanHandle.End),
                    "drag moves only the selected endpoint");
                Require(span.Layout(space.Radius).Start == initial.Start && span.EndOffset == new Vector3(1.2f,0,.9f),
                    "preview preserves authored span");
            }
#endif
            span.EndOffset = new(1.1f,0,0);
            space.Radius = 16;
            var whole = span.Layout(16);
            Require(whole.Bays == 5 && Math.Abs(whole.Placements().Last().Frame.P.AngleTo(whole.Start.P) * 16 - 15) < 1e-8,
                "whole-span cap snaps inward");
            span.FitMode = SpanFitMode.TrimFill;
            // Distinct continuous asset. Use a rigid bay as the geometry fixture
            // but a distinct PackedScene identity so the normal bays can be compared.
            span.FillScene = (PackedScene)span.SegmentScene!.Duplicate();
            var trim = span.Layout(16);
            var fill = trim.Placements().Single(p => p.Piece == SpanPiece.Fill);
            D4 FillEnd(float x)
            {
                var q = fill.Pose * (Vector3.Right * x);
                return (fill.Frame.P + fill.Frame.Local(q.X,q.Y,q.Z)/16).Unit();
            }
            Require(FillEnd(0).AngleTo(trim.At(15).P) < 1e-7 && FillEnd(3).AngleTo(trim.End) < 1e-7 &&
                trim.Placements().Last().Frame.P.AngleTo(trim.End) < 1e-12, "trim fill and exact cap");
            var trimmed = GlomeBaker.Bake(space, new GlomeBakeOptions(CaptureAuthoringSources: true)).AuthoringSources!;
            Require(trimmed.Evaluated.Where(p => p.Frame == fill.Frame).All(p => p.Pose.Basis == fill.Pose.Basis)
                && trimmed.Proxies.Count(p => p.Proxy.Chart == fill.Frame && p.Proxy.Pose.Basis == fill.Pose.Basis) == 2,
                "only designated fill and its proxies receive its longitudinal scale");
            span.EndOffset = Vector3.Right * Mathf.Pi;
            span.MidOffset = Vector3.Back;
            var opposite = span.Layout(16);
            Require(opposite.End.AngleTo(-opposite.Start.P) < 1e-12 &&
                opposite.At(opposite.Length/2).P.AngleTo(opposite.Start.Back) < 1e-12, "antipodal midpoint plane");
            span.EndOffset = Vector3.Right * (Mathf.Pi - .001f);
            var near = span.Layout(16);
            Require(near.ShowMidpoint && near.At(near.Length / 2).P.Dot(near.Start.Right) > .99999,
                "unique short arc near the antipode");
            span.EndOffset = Vector3.Zero;
            Require(span.Layout(16).Placements().Single().Piece == SpanPiece.EndCap, "zero run");
        }
        finally { space.Free(); }
        GD.Print("[GLOME-SPAN-PASS] R=16/32, transported rigid bays, source mapping, ray/sweep agreement, cap, trim and antipode");
    }

    private static void Require(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException("Geodesic span: " + why);
    }
}
