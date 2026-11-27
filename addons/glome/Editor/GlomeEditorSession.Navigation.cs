#nullable enable
#if TOOLS
using System;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private sealed record FocusMotion(S3Frame From, S3Frame To, double Elapsed);
    private FocusMotion? _focus;
    private Button _speedButton = null!;

    private Button Tool(Container parent, string title, string tip, Action action, bool toggle = false)
    {
        var button = new Button { Text = title, TooltipText = tip, ToggleMode = toggle,
            FocusMode = Control.FocusModeEnum.None };
        parent.AddChild(button);
        Listen(button, BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            action();
            _image.GrabFocus();
        }));
        return button;
    }

    private void AddNavigation(Container bar)
    {
        Tool(bar, "Reset View", "Return to the scene camera", Spawn);
        Tool(bar, "Focus", "Frame the selected anchor (F)", FocusSelected);
        Tool(bar, "From 3D", "Use the native 3D editor camera", () =>
        {
            var camera = EditorInterface.Singleton.GetEditorViewport3D().GetCamera3D();
            if (_bake != null && camera != null) SetView(FollowCamera(camera), null);
        });
        Tool(bar, "Origin", "Go to the origin", () => SetView(S3Frame.Identity, null));
        Tool(bar, "Antipode", "Jump to the exact opposite camera and pivot", () =>
        {
            if (_bake != null) SetView(GlomeAuthoring.Opposite(_view), -_pivot);
        });
        Tool(bar, "Level Horizon", "Level pitch and roll at the camera", () =>
            SetView(GlomeAuthoring.Level(_view), null));
        _speedButton = Tool(bar, "", "RMB + wheel changes fly speed. Click to reset.", () => SetSpeed(DefaultSpeed));
        SetSpeed(speed);
        Tool(bar, "Rebake", "Refresh the preview", () => RequestBake());
    }

    private void SetSpeed(double metres)
    {
        speed = double.IsFinite(metres) ? Math.Clamp(metres, .25, 128) : DefaultSpeed;
        _speedButton.Text = $"Speed: {speed:0.0} m/s";
    }

    private void Spawn()
    {
        if (viewData != null) SetView(viewData.InitialView, null);
    }

    // Decompose without changing the view's local orientation (including roll at -P).
    private void SetExact(S3Frame view)
    {
        var vertical = D4.Up.Reject(view.P);
        polar = vertical.Length < 0.126;
        _pitch = polar ? 0 : Math.Asin(Math.Clamp(-view.Back.Dot(vertical.Unit()), -1, 1));
        _horizon = view.Pitch(-_pitch);
    }

    private S3Anchor3D? Selected => EditorInterface.Singleton.GetSelection().GetSelectedNodes()
        .Select(GlomeAuthoring.AnchorOf).FirstOrDefault(anchor => anchor != null && GlomeChart.SpaceOf(anchor) == _space);

    private void FocusSelected()
    {
        if (_bake == null || Selected is not { } anchor) return;
        StopFly();
        CancelDrag();
        var target = Pivot(anchor);
        _pivot = target.P;
        _focus = new(_view, GlomeAuthoring.Focus(target, _space!.Radius, _extent.AroundPivot, lens), 0);
    }

    private void AdvanceFocus(double delta)
    {
        if (_focus is not { } motion) return;
        double elapsed = motion.Elapsed + delta;
        double t = Math.Min(1, elapsed / 0.22);
        SetExact(ChartCamera.Blend(motion.From, motion.To, t * t * (3 - 2 * t)));
        _focus = t < 1 ? motion with { Elapsed = elapsed } : null;
    }

    public bool FocusNode(Node3D node)
    {
        if (_bake == null || GlomeChart.SpaceOf(node) != _space) return false;
        double r = _space!.Radius;
        var anchor = GlomeAuthoring.AnchorOf(node);
        var target = anchor != null ? GlomeAuthoring.Pivot(anchor, r) : GlomeChart.At(node, r);
        double extent = anchor != null ? GlomeAuthoring.Measure(anchor, r).AroundPivot : 0;
        return SetView(GlomeAuthoring.Focus(target, r, extent, lens), target.P);
    }

    private bool HandleShortcut(InputEventKey key)
    {
        if (key.Keycode == Key.Escape)
        {
            bool handled = flying || drag != null || _focus != null;
            StopFly();
            CancelDrag();
            _focus = null;
            return handled;
        }
        if (flying || !_image.HasFocus() || _bake == null || key.Echo ||
            key.CtrlPressed || key.MetaPressed || key.AltPressed || key.ShiftPressed) return false;
        switch (key.Keycode)
        {
            case Key.F: FocusSelected(); return true;
            case Key.Q: SetTool(GizmoTool.Select); return true;
            case Key.W: SetTool(GizmoTool.Move); return true;
            case Key.E: SetTool(GizmoTool.Rotate); return true;
            default: return false;
        }
    }
}
#endif
