#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Glome.Core;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private readonly Control overlay = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private enum Handle { Ground, X, Z, Ring }
    private sealed record Drag(S3Anchor3D Anchor, Handle Handle, S3Frame Pivot, S3Frame Base,
        S3Frame Current, double[] Old, Vector2 Press, List<(D4 A, D4 B)> Edges);
    private Drag? drag;
    private S3Frame? displacement;
    private S3Anchor3D? selected;
    internal void SelectionChanged()
    {
        if (syncingSelection) return;
        dirty = true;
        selected = Selected;
        CancelDrag();
    }
    private void CancelDrag() { drag = null; displacement = null; overlay.QueueRedraw(); }
    private S3Frame Pivot(S3Anchor3D anchor)
    {
        var (frame, pose) = anchor.ChartPose(_bake!.Geometry.Radius);
        return ChartCamera.FromTangent(frame, pose, _bake.Geometry.Radius);
    }
    private bool Editable => _bake != null && GodotObject.IsInstanceValid(selected) &&
        selected!.Owner == plugin.GetTree().EditedSceneRoot;
    private bool Antipodal => Editable && displayed.P.AngleTo(Pivot(selected!).P) > Math.PI - .02;
    private Vector2 ImageSize => (Vector2)displayedSize * Mathf.Min(_image.Size.X / displayedSize.X, _image.Size.Y / displayedSize.Y);
    private double TanHalfFov => Math.Tan(double.DegreesToRadians(displayedFov) / 2);
    private bool Project(D4 point, out Vector2 screen)
    {
        screen = default;
        var depth = -point.Dot(displayed.Back);
        if (depth <= 1e-8) return false;
        var size = ImageSize;
        screen = _image.Size / 2 + new Vector2((float)point.Dot(displayed.Right), -(float)point.Dot(displayed.Up))
            * (float)(size.Y / (2 * depth * TanHalfFov));
        return new Rect2((_image.Size - size) / 2, size).HasPoint(screen);
    }
    private void Handles(S3Frame pivot, Action<Handle, Vector2, Vector2> segment)
    {
        void Curve(Handle handle, int count, Func<double, D4> sample)
        {
            var last = sample(0);
            for (int i = 1; i <= count; i++)
            {
                var next = sample((double)i / count);
                if (Project(last, out var a) && Project(next, out var b)) segment(handle, a, b);
                last = next;
            }
        }
        double r = _bake!.Geometry.Radius;
        Curve(Handle.X, 12, t => pivot.Travel(pivot.Right, t * 1.6 / r).P);
        Curve(Handle.Z, 12, t => pivot.Travel(pivot.Back, t * 1.6 / r).P);
        Curve(Handle.Ring, 64, t => pivot.Travel(pivot.Local(Math.Cos(t * Math.Tau), 0, Math.Sin(t * Math.Tau)), 1 / r).P);
    }
    private void DrawOverlay()
    {
        if (!Editable || Antipodal) return;
        var pivot = Pivot(selected!);
        if (displacement is { } delta && drag != null)
        {
            pivot = S3ChartMath.Compose(delta, drag.Pivot);
            foreach (var (a, b) in drag.Edges)
                if (Project(S3ChartMath.Apply(delta, a), out var start) && Project(S3ChartMath.Apply(delta, b), out var end))
                    overlay.DrawLine(start, end, Colors.White, 1, true);
        }
        Handles(pivot, (handle, a, b) => overlay.DrawLine(a, b, handle switch
        {
            Handle.X => Colors.Salmon,
            Handle.Z => Colors.DeepSkyBlue,
            _ => Colors.Gold
        }, 2, true));
        void Arrow(D4 axis, Color color)
        {
            double r = _bake!.Geometry.Radius;
            if (!Project(pivot.Travel(axis, 1.6 / r).P, out var tip) ||
                !Project(pivot.Travel(axis, 1.3 / r).P, out var tail) || tip.DistanceTo(tail) < 1) return;
            var direction = (tail - tip).Normalized() * 8;
            overlay.DrawColoredPolygon([tip, tip + direction.Rotated(.45f), tip + direction.Rotated(-.45f)], color);
        }
        Arrow(pivot.Right, Colors.Salmon);
        Arrow(pivot.Back, Colors.DeepSkyBlue);
        if (Project(pivot.P, out var center)) overlay.DrawCircle(center, 5, Colors.White);
    }
    private List<(D4, D4)> Wire(S3Anchor3D root)
    {
        List<(D4, D4)> edges = new();
        foreach (var anchor in GlomeChart.Descendants(root).OfType<S3Anchor3D>().Prepend(root))
        {
            S3Anchor3D? ancestor = anchor;
            while (ancestor != root && ancestor?.ChartPlacement == S3Anchor3D.PlacementMode.ParentChart)
            {
                Node? n = ancestor.GetParent();
                while (n != null && n is not S3Anchor3D) n = n.GetParent();
                ancestor = n as S3Anchor3D;
            }
            if (ancestor != root) continue;
            var (frame, transform) = anchor.ChartPose(_bake!.Geometry.Radius);
            GlomeChart.Walk(anchor, transform, (node, pose) =>
            {
                if (node is not MeshInstance3D { Mesh: not null, Visible: true } mesh) return;
                var box = mesh.GetAabb();
                D4 Corner(int i)
                {
                    var p = pose * box.GetEndpoint(i);
                    return (frame.P + frame.Local(p.X, p.Y, p.Z) / _bake.Geometry.Radius).Unit();
                }
                for (int i = 0; i < 8; i++)
                    for (int bit = 1; bit <= 4; bit *= 2)
                        if ((i & bit) == 0) edges.Add((Corner(i), Corner(i | bit)));
            });
        }
        return edges;
    }
    private bool DragInput(InputEvent input)
    {
        if (input is not InputEventMouse mouse || flying) return false;
        if (drag == null)
        {
            if (mouse is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || !Editable || Antipodal) return false;
            var anchor = selected!;
            var pivot = Pivot(anchor);
            Handle? hit = null;
            float best = 7;
            Handles(pivot, (h, a, b) =>
            {
                var d = b - a;
                if (d.LengthSquared() < .00001f) return;
                var distance = mouse.Position.DistanceTo(a + d * Mathf.Clamp((mouse.Position - a).Dot(d) / d.LengthSquared(), 0, 1));
                if (distance < best) { best = distance; hit = h; }
            });
            if (Project(pivot.P, out var center) && center.DistanceTo(mouse.Position) < 6) hit = Handle.Ground;
            if (hit == null) return false;
            _image.GrabFocus();
            drag = new(anchor, hit.Value, pivot, anchor.BasePose(_bake!.Geometry.Radius).Frame, anchor.ChartPose(_bake.Geometry.Radius).Frame,
                (double[])anchor.RelativeSpin.Clone(), mouse.Position, Wire(anchor));
            return true;
        }
        var g = drag;
        Vector2? Ground(Vector2 screen)
        {
            var uv = (screen - _image.Size / 2) * (float)(2 * TanHalfFov / ImageSize.Y);
            var dir = displayed.Local(uv.X, -uv.Y, -1).Unit();
            double a = displayed.P.Dot(g.Pivot.Up), b = dir.Dot(g.Pivot.Up);
            if (Math.Abs(a) < 1e-7) return null;
            var t = Math.Atan2(-a, b);
            if (t < 0) t += Math.PI;
            var point = displayed.P * Math.Cos(t) + dir * Math.Sin(t);
            if (point.Dot(g.Pivot.P) < .05) return null;
            var log = S3Frame.LogMap(g.Pivot.P, point) * _bake!.Geometry.Radius;
            return new((float)log.Dot(g.Pivot.Right), (float)log.Dot(g.Pivot.Back));
        }
        if (Ground(g.Press) is { } start && Ground(mouse.Position) is { } end)
        {
            var d = end - start;
            if (g.Handle == Handle.X) d.Y = 0;
            if (g.Handle == Handle.Z) d.X = 0;
            if (d.Length() > 1e-5)
            {
                var next = g.Handle == Handle.Ring ? g.Pivot.Yaw(end.Angle() - start.Angle())
                    : g.Pivot.Travel(g.Pivot.Local(d.X, 0, d.Y).Unit(), d.Length() / _bake!.Geometry.Radius);
                displacement = S3ChartMath.Compose(next, S3ChartMath.Transpose(g.Pivot));
            }
            else displacement = null;
        }
        overlay.QueueRedraw();
        if (mouse is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
        {
            var delta = displacement;
            CancelDrag();
            if (delta.HasValue)
            {
                var relative = S3ChartMath.Compose(S3ChartMath.Transpose(g.Base), S3ChartMath.Compose(delta.Value, g.Current));
                var undo = plugin.GetUndoRedo();
                undo.CreateAction(g.Handle == Handle.Ring ? "Rotate S³ anchor" : "Move S³ anchor", customContext: plugin.GetTree().EditedSceneRoot);
                undo.AddDoProperty(g.Anchor, nameof(g.Anchor.RelativeSpin), S3ChartMath.Encode(relative));
                undo.AddUndoProperty(g.Anchor, nameof(g.Anchor.RelativeSpin), g.Old);
                undo.AddDoMethod(_space!, nameof(S3Space3D.RequestRebake));
                undo.AddUndoMethod(_space!, nameof(S3Space3D.RequestRebake));
                undo.CommitAction();
                _needsBake = true;
            }
        }
        return true;
    }
}
#endif
