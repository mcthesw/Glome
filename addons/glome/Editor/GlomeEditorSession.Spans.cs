#nullable enable
#if TOOLS
using System;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private GlomeSpanDrag? _spanDrag;

    private GeodesicSpanLayout SelectedSpan(S3GeodesicSpan3D span) =>
        _spanDrag?.Preview ?? span.Layout(drag?.PreviewPivot ?? Pivot(span), _space!.Radius, span.EndOffset, span.MidOffset);

    private static void SpanHandles(GeodesicSpanLayout layout, Action<SpanHandle, D4> visit)
    {
        visit(SpanHandle.Start, layout.Start.P);
        if (layout.ShowMidpoint) visit(SpanHandle.Midpoint, layout.At(layout.Length / 2).P);
        visit(SpanHandle.End, layout.End);
    }

    private void DrawSpan(S3GeodesicSpan3D span)
    {
        var layout = SelectedSpan(span);
        var previous = layout.Start.P;
        int steps = Math.Max(1, (int)Math.Ceiling(layout.Angle / .04));
        var pathColor = Colors.Gold;
        for (int i = 1; i <= steps; i++)
        {
            var point = layout.At(layout.Length * i / steps).P;
            Line(previous, point, pathColor, 2);
            previous = point;
        }
        if (_spanDrag != null && displayedBake?.AuthoringSources?.SpanTemplates is { } templates)
            foreach (var placement in layout.Placements())
            {
                if (span.Asset(placement.Piece) is not { } scene || !templates.TryGetValue(scene.GetInstanceId(), out var template)) continue;
                D4 At(Vector3 v) => GlomeNodeEditing.Lift(placement.Frame, placement.Pose * v, _space!.Radius);
                int perBay = Math.Max(24, 6000 / Math.Max(1, layout.Bays));
                int stride = Math.Max(1, (template.Edges.Length - 1) / perBay + 1);
                for (int i = 0; i < template.Edges.Length; i += stride)
                {
                    var edge = template.Edges[i];
                    Line(At(edge.A), At(edge.B), Colors.Aquamarine);
                }
                if (Shows(OverlayItem.Collision))
                    foreach (var proxy in template.Proxies) DrawProxy(placement.Place(proxy));
            }
        var font = overlay.GetThemeDefaultFont();
        int fontSize = overlay.GetThemeDefaultFontSize();
        SpanHandles(layout, (handle, point) =>
        {
            if (!Project(point, out var p)) return;
            var color = handle == SpanHandle.Start ? Colors.Aquamarine : pathColor;
            overlay.DrawCircle(p, 7, Colors.Black, antialiased: true);
            overlay.DrawArc(p, 6, 0, Mathf.Tau, 24, color, 2, true);
            overlay.DrawCircle(p, 2, color, antialiased: true);
            var label = p + new Vector2(10, -6);
            overlay.DrawStringOutline(font, label, handle.ToString(), fontSize: fontSize, size: 3, modulate: Colors.Black);
            overlay.DrawString(font, label, handle.ToString(), fontSize: fontSize, modulate: color);
        });
    }

    private bool SpanInput(InputEventMouse mouse, S3GeodesicSpan3D span)
    {
        if (_spanDrag == null)
        {
            if (mouse is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || !Editable) return false;
            SpanHandle? hit = null;
            float best = 9;
            SpanHandles(SelectedSpan(span), (handle, point) =>
            {
                if (!Project(point, out var p)) return;
                float distance = p.DistanceTo(mouse.Position);
                if (distance <= best) { best = distance; hit = handle; }
            });
            if (hit == null) return false;
            _image.GrabFocus();
            _spanDrag = new(span, hit.Value, displayed, mouse.Position, _space!.Radius);
        }
        else
        {
            if (!Editable) { CancelDrag(); return true; }
            var g = _spanDrag;
            if (InDragPlane(g.Pivot, g.Plane, g.Last) is { } from && InDragPlane(g.Pivot, g.Plane, mouse.Position) is { } to)
                g.Metres += (to - from) * (mouse.ShiftPressed ? .1f : 1);
            g.Last = mouse.Position;
            g.Move(mouse.CtrlPressed || _snap.ButtonPressed);
            if (mouse is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            {
                CancelDrag();
                if (g.Spin != null || g.End != span.EndOffset || g.Mid != span.MidOffset)
                {
                    var undo = plugin.GetUndoRedo();
                    undo.CreateAction($"Move {span.Name} {g.Handle}", customContext: plugin.GetTree().EditedSceneRoot);
                    if (g.Spin is { } spin)
                    {
                        undo.AddDoProperty(span, nameof(span.RelativeSpin), GlomeAuthoring.DisplacedSpin(span, spin, _space!.Radius));
                        undo.AddUndoProperty(span, nameof(span.RelativeSpin), span.RelativeSpin);
                    }
                    if (g.End != span.EndOffset)
                    {
                        undo.AddDoProperty(span, nameof(span.EndOffset), g.End);
                        undo.AddUndoProperty(span, nameof(span.EndOffset), span.EndOffset);
                    }
                    if (g.Mid != span.MidOffset)
                    {
                        undo.AddDoProperty(span, nameof(span.MidOffset), g.Mid);
                        undo.AddUndoProperty(span, nameof(span.MidOffset), span.MidOffset);
                    }
                    FinishAction(undo);
                }
            }
        }
        UpdateHud();
        overlay.QueueRedraw();
        return true;
    }
}
#endif
