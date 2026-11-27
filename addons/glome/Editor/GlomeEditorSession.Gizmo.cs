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
    private sealed class Drag(S3Anchor3D anchor, GizmoHandle handle, S3Frame pivot, GizmoPlane plane, Vector2 press, List<(D4, D4)> edges)
    {
        public S3Anchor3D Anchor { get; } = anchor;
        public GizmoHandle Handle { get; } = handle;
        public GizmoPlane Plane { get; } = plane;
        public S3Frame Pivot { get; } = pivot;
        public List<(D4, D4)> Edges { get; } = edges;
        public Vector2 Last = press, Metres;
        public double Radians;
    }
    private Drag? drag;
    private S3Frame? displacement;
    private S3Anchor3D? selected;
    internal void SelectionChanged()
    {
        if (syncingSelection) return;
        dirty = true;
        selected = Selected;
        _focus = null;
        CancelDrag();
        RefreshSelectionExtent();
    }
    private void CancelDrag()
    {
        drag = null;
        displacement = null;
        _arcDelta = _angleDelta = 0;
        _delta.Visible = false;
        overlay.QueueRedraw();
    }
    private S3Frame Pivot(S3Anchor3D anchor) => GlomeAuthoring.Pivot(anchor, _space!.Radius);
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
    private void Handles(S3Frame pivot, Action<GizmoHandle, Vector2, Vector2> segment)
    {
        void Curve(GizmoHandle handle, int count, Func<double, D4> sample)
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
        if (_tool == GizmoTool.Select) return;
        if (_tool == GizmoTool.Rotate)
        {
            foreach (var ring in new[] { GizmoHandle.Pitch, GizmoHandle.Yaw, GizmoHandle.Roll })
            {
                var plane = GizmoPlane.For(pivot, ring, displayed.P);
                Curve(ring, 64, t => pivot.Travel(plane.U * Math.Cos(t * Math.Tau) + plane.V * Math.Sin(t * Math.Tau), 1 / r).P);
            }
            return;
        }
        Curve(GizmoHandle.X, 12, t => pivot.Travel(pivot.Right, t * 1.6 / r).P);
        Curve(GizmoHandle.Y, 12, t => pivot.Travel(pivot.Up, t * 1.6 / r).P);
        Curve(GizmoHandle.Z, 12, t => pivot.Travel(pivot.Back, t * 1.6 / r).P);
    }
    private void DrawOverlay()
    {
        DrawPins();
        if (_bake == null || !GodotObject.IsInstanceValid(selected)) return;
        var pivot = Pivot(selected!);
        var transform = displacement ?? S3Frame.Identity;
        if (drag != null) pivot = S3ChartMath.Compose(transform, drag.Pivot);
        foreach (var (a, b) in drag?.Edges ?? _bounds)
            if (Project(S3ChartMath.Apply(transform, a), out var start) && Project(S3ChartMath.Apply(transform, b), out var end))
                overlay.DrawLine(start, end, Colors.White, 1, true);
        if (!Editable || Antipodal || _tool == GizmoTool.Select) return;
        Handles(pivot, (handle, a, b) => overlay.DrawLine(a, b, handle switch
        {
            GizmoHandle.X or GizmoHandle.Pitch => Colors.Salmon,
            GizmoHandle.Y or GizmoHandle.Yaw => Colors.LightGreen,
            _ => Colors.DeepSkyBlue
        }, 2, true));
        void Arrow(D4 axis, Color color)
        {
            double r = _bake!.Geometry.Radius;
            if (!Project(pivot.Travel(axis, 1.6 / r).P, out var tip) ||
                !Project(pivot.Travel(axis, 1.3 / r).P, out var tail) || tip.DistanceTo(tail) < 1) return;
            var direction = (tail - tip).Normalized() * 8;
            overlay.DrawColoredPolygon([tip, tip + direction.Rotated(.45f), tip + direction.Rotated(-.45f)], color);
        }
        if (_tool != GizmoTool.Move) return;
        Arrow(pivot.Right, Colors.Salmon);
        Arrow(pivot.Up, Colors.LightGreen);
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
            if (mouse is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || _tool == GizmoTool.Select || !Editable || Antipodal) return false;
            var anchor = selected!;
            var pivot = Pivot(anchor);
            GizmoHandle? hit = null;
            float best = 7;
            Handles(pivot, (h, a, b) =>
            {
                var d = b - a;
                if (d.LengthSquared() < .00001f) return;
                var distance = mouse.Position.DistanceTo(a + d * Mathf.Clamp((mouse.Position - a).Dot(d) / d.LengthSquared(), 0, 1));
                if (distance < best) { best = distance; hit = h; }
            });
            if (_tool == GizmoTool.Move && Project(pivot.P, out var center) && center.DistanceTo(mouse.Position) < 6) hit = GizmoHandle.Ground;
            if (hit == null) return false;
            _image.GrabFocus();
            drag = new(anchor, hit.Value, pivot, GizmoPlane.For(pivot, hit.Value, displayed.P), mouse.Position, _showBounds.ButtonPressed ? _bounds : Wire(anchor));
            UpdateHud();
            return true;
        }
        var g = drag;
        Vector2? InDragPlane(Vector2 screen)
        {
            var uv = (screen - _image.Size / 2) * (float)(2 * TanHalfFov / ImageSize.Y);
            var dir = displayed.Local(uv.X, -uv.Y, -1).Unit();
            double a = displayed.P.Dot(g.Plane.Normal), b = dir.Dot(g.Plane.Normal);
            if (Math.Abs(a) < 1e-7) return null;
            var t = Math.Atan2(-a, b);
            if (t < 0) t += Math.PI;
            var point = displayed.P * Math.Cos(t) + dir * Math.Sin(t);
            if (point.Dot(g.Pivot.P) < .05) return null;
            var log = S3Frame.LogMap(g.Pivot.P, point) * _bake!.Geometry.Radius;
            return new((float)log.Dot(g.Plane.U), (float)log.Dot(g.Plane.V));
        }
        var pixels = mouse.Position - g.Last;
        double precision = mouse.ShiftPressed ? 0.1 : 1;
        if (InDragPlane(g.Last) is { } start && InDragPlane(mouse.Position) is { } end)
        {
            g.Metres += (end - start) * (float)precision;
            g.Radians += start.AngleTo(end) * precision;
        }
        else
        {
            // Edge-on chart: use the visible axis when possible and vertical drag for depth.
            float metresPerPixel = (float)(2 * TanHalfFov * Math.Sin(displayed.P.AngleTo(g.Pivot.P))
                * _space!.Radius / Math.Max(1, ImageSize.Y));
            D4 tangent = displayed.Local(pixels.X, -pixels.Y, 0);
            float Along(D4 axis) => Math.Abs(axis.Dot(displayed.Back)) > 0.9
                ? pixels.Y * Math.Sign(axis.Dot(displayed.Back)) : (float)tangent.Dot(axis);
            g.Metres += new Vector2(Along(g.Plane.U), Along(g.Plane.V)) * metresPerPixel * (float)precision;
            g.Radians += pixels.X * .01 * precision;
        }
        g.Last = mouse.Position;
        bool snap = mouse.CtrlPressed || _snap.ButtonPressed;
        var movement = g.Metres;
        bool rotate = GizmoPlane.IsRing(g.Handle);
        if (g.Handle != GizmoHandle.Ground) movement.Y = 0;
        _arcDelta = rotate ? 0 : GlomeAuthoring.ArcStep(movement.Length(), snap);
        _angleDelta = rotate ? GlomeAuthoring.AngleStep(g.Radians, snap) : 0;
        var next = g.Pivot;
        if (_arcDelta > 1e-9)
            next = g.Pivot.Travel((g.Plane.U * movement.X + g.Plane.V * movement.Y).Unit(), _arcDelta / _space!.Radius);
        else if (Math.Abs(_angleDelta) > 1e-9)
            next = GizmoPlane.Turn(g.Pivot, g.Handle, _angleDelta);
        displacement = next == g.Pivot ? null : GlomeAuthoring.Between(g.Pivot, next);
        UpdateHud();
        overlay.QueueRedraw();
        if (mouse is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
        {
            var delta = displacement;
            CancelDrag();
            if (delta.HasValue && Editable)
                CommitSpin(rotate ? "Rotate S³ anchor" : "Move S³ anchor", g.Anchor, delta.Value);
        }
        return true;
    }
}
#endif
