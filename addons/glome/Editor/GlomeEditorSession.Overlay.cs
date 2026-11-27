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
    private enum GizmoTool { Select, Move, Rotate }
    private GizmoTool _tool;
    private readonly Dictionary<GizmoTool, Button> _toolButtons = [];
    private Button _snap = null!, _showPins = null!, _showBounds = null!;
    private readonly VBoxContainer _hud = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _location = HudLabel(), _distortion = HudLabel(), _delta = HudLabel();
    private (S3Anchor3D Node, S3Frame Pose)[] _pins = [];
    private List<(D4, D4)> _bounds = [];
    private GlomeAuthoring.Extent _extent;
    private double _arcDelta, _angleDelta;

    private static Label HudLabel()
    {
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeConstantOverride("outline_size", 4);
        return label;
    }

    private void AddAuthoringTools(Container bar)
    {
        foreach (var tool in Enum.GetValues<GizmoTool>())
        {
            string binding = tool switch { GizmoTool.Select => "Q", GizmoTool.Move => "W", _ => "E" };
            _toolButtons.Add(tool, Tool(bar, tool.ToString(), $"{tool} ({binding})", () => SetTool(tool), true));
        }
        SetTool(GizmoTool.Move);
        _snap = Tool(bar, "Snap", "Snap to 0.5 m or 15° (Ctrl). Hold Shift for fine motion.", () => { }, true);
        _showPins = Tool(bar, "Anchors", "Show and pick anchor pins and axes", overlay.QueueRedraw, true);
        _showPins.ButtonPressed = true;
        _showBounds = Tool(bar, "Bounds", "Show selected mesh bounds", () =>
        {
            RefreshBounds();
            overlay.QueueRedraw();
        }, true);
        AddAnchorMenu(bar);
        _image.AddChild(_hud);
        _hud.Position = new(10, 8);
        _hud.AddChild(_location);
        _hud.AddChild(_distortion);
        _hud.AddChild(_delta);
        _hud.Visible = false;
    }

    private void SetTool(GizmoTool tool)
    {
        _tool = tool;
        foreach (var (mode, button) in _toolButtons) button.SetPressedNoSignal(mode == tool);
        CancelDrag();
    }

    private void CacheAnchors(double radius)
    {
        _charts = GlomeChart.Descendants(_space!).OfType<S3Anchor3D>().ToArray();
        _pins = _charts.Select(a => (a, GlomeAuthoring.Pivot(a, radius))).ToArray();
    }

    private void RefreshSelectionExtent()
    {
        _extent = _space != null && GodotObject.IsInstanceValid(selected)
            ? GlomeAuthoring.Measure(selected!, _space.Radius) : default;
        RefreshBounds();
        UpdateHud();
    }

    private void RefreshBounds() => _bounds = _bake != null && GodotObject.IsInstanceValid(selected)
        && _showBounds.ButtonPressed ? Wire(selected!) : [];

    private void UpdateHud()
    {
        if (_space == null || _bake == null) { _hud.Visible = false; return; }
        _hud.Visible = true;
        double r = _space.Radius;
        string space = $"R = {r:0.#} m  |  2πR = {Math.Tau * r:0.0} m";
        bool valid = GodotObject.IsInstanceValid(selected);
        _distortion.Visible = valid;
        _delta.Visible = drag != null;
        if (!valid) { _location.Text = space; return; }
        var pivot = drag?.Pivot ?? Pivot(selected!);
        if (displacement is { } delta) pivot = S3ChartMath.Compose(delta, pivot);
        double theta = pivot.P.AngleTo(D4.Pole);
        _location.Text = $"{selected!.Name}  |  {selected.ChartPlacement}  |  d = {r * theta:0.0} m  |  θ = {double.RadiansToDegrees(theta):0.0}°\n{space}";
        bool warn = _extent.Distorted(r);
        _distortion.Modulate = warn ? Colors.Orange : Colors.White;
        _distortion.Text = $"Chart extent {_extent.ChartMetres:0.00} m  |  {_extent.Compression(r):0.0}% compression"
            + (warn ? "  |  Exceeds R/3" : "");
        if (drag != null) _delta.Text = $"Δarc = {_arcDelta:0.00} m  |  Δdeg = {double.RadiansToDegrees(_angleDelta):0.0}°";
    }

    private void DrawPins()
    {
        if (!_showPins.ButtonPressed || displayedBake == null) return;
        foreach (var (anchor, cached) in _pins)
        {
            if (!GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree()) continue;
            var pose = anchor == selected && drag != null && displacement is { } move
                ? S3ChartMath.Compose(move, drag.Pivot) : cached;
            if (!Project(pose.P, out var p)) continue;
            void Axis(D4 axis, Color color)
            {
                var end = new ArcRay(pose.P, axis).At(.42 / displayedBake.Geometry.Radius);
                if (Project(end, out var q)) overlay.DrawLine(p, q, color, 1, true);
            }
            Axis(pose.Right, Colors.LightCoral);
            Axis(pose.Up, Colors.DarkSeaGreen);
            Axis(pose.Back, Colors.LightSkyBlue);
            float width = anchor == selected ? 5 : 3.5f;
            Vector2[] diamond = [p + Vector2.Up * width, p + Vector2.Right * width, p + Vector2.Down * width,
                p + Vector2.Left * width, p + Vector2.Up * width];
            overlay.DrawPolyline(diamond, Colors.Black, 3, true);
            overlay.DrawPolyline(diamond, anchor == selected ? Colors.Gold : Colors.LightGray, 1, true);
        }
    }

    private bool PickAnchorPin(Vector2 mouse)
    {
        if (!_showPins.ButtonPressed || displayedBake == null) return false;
        S3Anchor3D? hit = null;
        float best = 7;
        double arc = Math.PI;
        foreach (var (node, frame) in _pins)
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree() || !Project(frame.P, out var pos)) continue;
            float pixels = pos.DistanceTo(mouse);
            double distance = displayed.P.AngleTo(frame.P);
            if (pixels > best || pixels == best && distance >= arc) continue;
            (hit, best, arc) = (node, pixels, distance);
        }
        if (hit == null) return false;
        var selection = EditorInterface.Singleton.GetSelection();
        selection.Clear();
        selection.AddNode(hit);
        EditorInterface.Singleton.InspectObject(hit);
        _pivot = Pivot(hit).P;
        UpdateDistance();
        return true;
    }
}
#endif
