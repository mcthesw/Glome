#nullable enable
#if TOOLS
using System;
using Godot;
using Glome.Core;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private readonly Control overlay = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private GlomeGizmoDrag? drag;
    private Node3D? selected;
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
        _arcDelta = _angleDelta = 0;
        _delta.Visible = false;
        overlay.QueueRedraw();
    }

    private S3Frame Pivot(Node3D node) => GlomeAuthoring.Pivot(node, _space!.Radius);
    private bool Editable => displayedBake != null && displayedBake == _bake && GodotObject.IsInstanceValid(selected) &&
        selected!.IsInsideTree() && GlomeNodeEditing.EditableTarget(selected, plugin.GetTree().EditedSceneRoot) == selected &&
        GlomeNodeEditing.Invertible(GlomeNodeEditing.Pose(selected, _space!.Radius).Pose.Basis);
    private bool Antipodal => Editable && displayed.P.AngleTo(Pivot(selected!).P) > Math.PI - .02;
    private Vector2 ImageSize => (Vector2)displayedSize * Mathf.Min(_image.Size.X / displayedSize.X, _image.Size.Y / displayedSize.Y);
    private double TanHalfFov => Math.Tan(double.DegreesToRadians(displayedFov) / 2);
    private bool Project(D4 point, out Vector2 screen)
    {
        screen = default;
        double depth = -point.Dot(displayed.Back);
        if (depth <= 1e-8) return false;
        var size = ImageSize;
        screen = _image.Size / 2 + new Vector2((float)point.Dot(displayed.Right), -(float)point.Dot(displayed.Up))
            * (float)(size.Y / (2 * depth * TanHalfFov));
        return new Rect2((_image.Size - size) / 2, size).HasPoint(screen);
    }

    private void DrawOverlay()
    {
        DrawPins();
        DrawDevices();
        if (_bake == null || !GodotObject.IsInstanceValid(selected)) return;
        DrawCollision();
        var pivot = drag?.PreviewPivot ?? Pivot(selected!);
        if ((drag?.Wire ?? _bounds) is { } wire)
        {
            var pose = drag?.ChartPose ?? GlomeNodeEditing.Pose(selected!, _space!.Radius).Pose;
            foreach (var edge in wire.Edges)
            {
                D4 Point(Vector3 v)
                {
                    var p = GlomeNodeEditing.Lift(edge.Chart, pose * v, _bake.Geometry.Radius);
                    return drag?.Spin is { } spin ? S3ChartMath.Apply(spin, p) : p;
                }
                Line(Point(edge.A), Point(edge.B), Colors.White);
            }
        }
        if (!Editable || Antipodal || _tool == GizmoTool.Select) return;
        Handles(pivot, (handle, a, b) => overlay.DrawLine(a, b, HandleColor(handle), 2, true));
    }

    private bool DragInput(InputEvent input)
    {
        if (input is not InputEventMouse mouse || flying) return false;
        if (drag == null)
        {
            if (mouse is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } ||
                _tool == GizmoTool.Select || !Editable || Antipodal) return false;
            var pivot = Pivot(selected!);
            var hit = HitHandle(pivot, mouse.Position);
            if (hit == null) return false;
            _image.GrabFocus();
            drag = new(selected!, hit.Value, displayed, mouse.Position, displayedBake!);
            UpdateHud();
            return true;
        }
        var g = drag;
        if (!Editable) { CancelDrag(); return true; }
        var pixels = mouse.Position - g.Last;
        float precision = mouse.ShiftPressed ? .1f : 1;
        g.Pixels += pixels * precision;
        if (InDragPlane(g, g.Last) is { } start && InDragPlane(g, mouse.Position) is { } end)
        {
            g.Metres += (end - start) * precision;
            g.Radians += start.AngleTo(end) * precision;
        }
        else
        {
            float metresPerPixel = (float)(2 * TanHalfFov * Math.Sin(displayed.P.AngleTo(g.Pivot.P))
                * _space!.Radius / Math.Max(1, ImageSize.Y));
            D4 tangent = displayed.Local(pixels.X, -pixels.Y, 0);
            float Along(D4 axis) => Math.Abs(axis.Dot(displayed.Back)) > .9
                ? pixels.Y * Math.Sign(axis.Dot(displayed.Back)) : (float)tangent.Dot(axis);
            g.Metres += new Vector2(Along(g.Plane.U), Along(g.Plane.V)) * metresPerPixel * precision;
            g.Radians += pixels.X * .01 * precision;
        }
        g.Last = mouse.Position;
        g.Preview(_space!.Radius, _tool == GizmoTool.Scale, mouse.CtrlPressed || _snap.ButtonPressed, out _arcDelta, out _angleDelta);
        UpdateHud();
        overlay.QueueRedraw();
        if (mouse is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
        {
            CancelDrag();
            if (g.Spin is { } spin) CommitSpin($"{_tool} S³ anchor", (S3Anchor3D)g.Node, spin);
            else if (g.Scalar != g.InitialScalar)
                CommitProperty(g.Node, g.Handle == GizmoHandle.Fov ? nameof(S3Camera3D.Fov) : "omni_range", g.Scalar, g.InitialScalar);
            else if (g.Local != g.Initial) CommitProperty(g.Node, "transform", g.Local, g.Initial);
        }
        return true;
    }

    private Vector2? InDragPlane(GlomeGizmoDrag g, Vector2 screen)
    {
        var uv = (screen - _image.Size / 2) * (float)(2 * TanHalfFov / ImageSize.Y);
        var dir = displayed.Local(uv.X, -uv.Y, -1).Unit();
        double a = displayed.P.Dot(g.Plane.Normal), b = dir.Dot(g.Plane.Normal);
        if (Math.Abs(a) < 1e-7) return null;
        double t = Math.Atan2(-a, b);
        if (t < 0) t += Math.PI;
        var point = new ArcRay(displayed.P, dir).At(t);
        if (point.Dot(g.Pivot.P) < .05) return null;
        var log = S3Frame.LogMap(g.Pivot.P, point) * _space!.Radius;
        return new((float)log.Dot(g.Plane.U), (float)log.Dot(g.Plane.V));
    }

    private void CommitProperty(Node3D node, string property, Variant value, Variant before)
    {
        var undo = plugin.GetUndoRedo();
        undo.CreateAction($"Edit {node.Name} {property}", customContext: plugin.GetTree().EditedSceneRoot);
        undo.AddDoProperty(node, property, value);
        undo.AddUndoProperty(node, property, before);
        TrackProperty(undo, node, property);
        undo.CommitAction();
    }
}
#endif
