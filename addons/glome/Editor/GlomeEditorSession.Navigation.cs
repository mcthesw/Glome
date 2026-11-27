#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private sealed record FocusMotion(S3Frame From, S3Frame To, double Elapsed);
    private FocusMotion? _focus;
    private readonly Label _speedBadge = new()
    {
        Name = "FlySpeed", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
        SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
    };
    private readonly Timer _speedTimer = new() { OneShot = true, WaitTime = 1.5 };

    private void AddViewMenu(PopupMenu popup)
    {
        var actions = new Dictionary<long, Action>();
        void Add(string label, Action action)
        {
            int id = popup.ItemCount;
            popup.AddItem(label, id);
            actions.Add(id, action);
        }

        Add("Focus Selection (F)", FocusSelected);
        Add("Reset View", Spawn);
        Add("From 3D", () =>
        {
            var camera = EditorInterface.Singleton.GetEditorViewport3D().GetCamera3D();
            if (_bake != null && camera != null) SetView(FollowCamera(camera), null);
        });
        popup.AddSeparator();
        Add("Go to Origin", () => SetView(S3Frame.Identity, null));
        Add("Go to Antipode", () =>
        {
            if (_bake != null) SetView(GlomeAuthoring.Opposite(_view), -_pivot);
        });
        Add("Level Horizon", () => SetView(GlomeAuthoring.Level(_view), null));
        popup.AddSeparator();
        AddBookmarks(popup);
        int cameraItems = popup.ItemCount;
        popup.AddSeparator();
        Add("Reset Fly Speed", () => SetSpeed(DefaultSpeed, showBadge: true));
        Add("Rebake Preview", () => RequestBake());
        Listen(popup, Window.SignalName.AboutToPopup, Callable.From(() =>
        {
            for (int i = 0; i < cameraItems; i++) popup.SetItemDisabled(i, _bake == null);
            popup.SetItemDisabled(0, _bake == null || Selected == null);
        }));
        Listen(popup, PopupMenu.SignalName.IdPressed, Callable.From<long>(id => actions[id]()));
    }

    private void SetSpeed(double metres, bool showBadge = false)
    {
        speed = double.IsFinite(metres) ? Math.Clamp(metres, .25, 128) : DefaultSpeed;
        _speedBadge.Visible = showBadge;
        if (showBadge)
        {
            _speedBadge.Text = speed < 1 ? $"{speed:0.00} m/s" : $"{speed:0.0} m/s";
            _speedTimer.Start();
        }
        else _speedTimer.Stop();
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

    private Node3D? Selected => EditorInterface.Singleton.GetSelection().GetSelectedNodes()
        .Select(node => GlomeNodeEditing.EditableTarget(node, plugin.GetTree().EditedSceneRoot))
        .FirstOrDefault(node => node != null && GlomeChart.SpaceOf(node) == _space);

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
        node = GlomeNodeEditing.EditableTarget(node, plugin.GetTree().EditedSceneRoot) ?? node;
        var target = GlomeAuthoring.Pivot(node, r);
        double extent = GlomeAuthoring.Measure(node, r).AroundPivot;
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
        if (flying || !_image.HasFocus() || _bake == null || key.Echo || key.AltPressed || key.ShiftPressed) return false;
        if (key.CtrlPressed || key.MetaPressed)
        {
            if (key.Keycode != Key.D || !Editable || drag != null) return false;
            Insert("Duplicate S³ node", [GlomeScenePlacement.Duplicate(selected!, plugin.GetTree().EditedSceneRoot)]);
            return true;
        }
        switch (key.Keycode)
        {
            case Key.F: FocusSelected(); return true;
            case Key.Q: SetTool(GizmoTool.Select); return true;
            case Key.W: SetTool(GizmoTool.Move); return true;
            case Key.E: SetTool(GizmoTool.Rotate); return true;
            case Key.R: SetTool(GizmoTool.Scale); return true;
            default: return false;
        }
    }
}
#endif
