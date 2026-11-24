#nullable enable
#if TOOLS
using System;
using System.Linq;
using System.Collections.Generic;
using Godot;
using Glome.Core;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession : IDisposable
{
    private readonly GlomeEditorPlugin plugin;
    private readonly VBoxContainer panel = new() { Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private S3BakeSnapshot? displayedBake;
    private S3Frame submitted;
    private (S3BakeSnapshot Bake, S3Frame View, Vector2I Size, double Fov)? awaitingPresentation;
    private bool flying, syncingSelection, disposed;
    private double speed = 8;
    private S3Frame displayed;
    private TextureRect _image = null!;

    private Label _status = null!;

    private S3Space3D? _space;
    private S3Anchor3D[] _charts = [];
    private GlomeComputeRenderer? _renderer;
    private S3BakeSnapshot? _bake;
    private S3Frame _horizon = S3Frame.Identity;
    private double _pitch;
    private D4 _pivot;
    private double _pivotDist = 4.85;
    private bool polar;
    private static readonly Vector2I FallbackSize = new(640, 360);
    private Vector2I outputSize = FallbackSize;
    private Vector2I pendingSize;
    private double stableSeconds;

    private Vector2I TargetSize()
    {
        var toPixels = _image.GetViewport().GetFinalTransform() * _image.GetGlobalTransformWithCanvas();
        var panelSize = _image.Size * toPixels.Scale.Abs();
        if (panelSize.X < 1 || panelSize.Y < 1) return FallbackSize;
        return new(Mathf.RoundToInt(panelSize.X), Mathf.RoundToInt(panelSize.Y));
    }

    private Vector2I displayedSize = FallbackSize;
    private bool dirty = true;
    private GlomeEditorView? viewData;
    private IGlomeEditorViewProvider[] providers = [];
    private readonly OptionButton spaces = new() { Name = "Space" };
    private S3Space3D[] spaceNodes = [];
    private IGlomeEditorViewProvider Provider => providers[mode.Selected];
    private double lens = GlomeComputeRenderer.VerticalFov, displayedFov = GlomeComputeRenderer.VerticalFov;

    private void UpdateResolution(double delta)
    {
        var target = Provider.Resolution(TargetSize());
        if (pendingSize != target) { pendingSize = target; stableSeconds = 0; }
        else stableSeconds += delta;
        if (stableSeconds < 0.1) return;
        if (target == outputSize) return;
        dirty = true;
        _renderer!.Resize(target);
        outputSize = target;
    }

    private readonly OptionButton mode = new() { Name = "ViewMode" };
    private S3Frame _view
    {
        get => _horizon.Pitch(_pitch);
        set => Aim(value, -value.Back);
    }

    private void Aim(S3Frame transported, D4 gaze)
    {
        var p = transported.P.Unit();
        double latitude = Math.Abs(p.Y);
        if (latitude > .992) polar = true;
        var up = transported.Up.Reject(p).Unit();
        if (latitude <= .992)
        {
            var target = D4.Up.Reject(p).Unit();
            double separation = up.AngleTo(target);
            if (!polar || separation < 1e-8) up = target;
            else
            {
                // Ease the transported vertical back to geographic up on cap exit.
                double turn = Math.Min(separation, Math.Clamp((.992-latitude)/.006, 0, 1)*.12);
                var tangent = target.Reject(up);
                if (tangent.Length < 1e-9) tangent = transported.Right.Reject(p).Reject(up);
                up = new ArcRay(up, tangent.Unit()).At(turn);
                if (latitude <= .986 && turn >= separation) polar = false;
            }
        }
        var back = (-gaze).Reject(p).Reject(up);
        if (back.Length < 1e-9) back = transported.Back.Reject(p).Reject(up);
        back = back.Unit();
        _horizon = new(-D4.Hodge(up, back, p), up, back, p);
        _pitch = Math.Clamp(Math.Asin(Math.Clamp(gaze.Dot(up), -1, 1)), -Math.PI*89/180, Math.PI*89/180);
    }

    private void VirtualPivot()
    {
        _pivotDist = 4.85;
        _pivot = new ArcRay(_view.P, -_view.Back).At(_pivotDist / _space!.Radius);
    }

    private void UpdateDistance() => _pivotDist = _view.P.AngleTo(_pivot) * _bake!.Geometry.Radius;

    private void Orbit(Vector2 delta)
    {
        double r = _bake!.Geometry.Radius;
        double angle = Math.Clamp(_pivotDist, .35, Math.PI*r-.35)/r;
        var radial = _view.P.Reject(_pivot);
        if (radial.Length < 1e-9) return;
        radial = radial.Unit();
        var axis = (_view.Right * -delta.X + _view.Up * delta.Y).Reject(_pivot).Reject(radial);
        if (axis.Length < 1e-9) return;
        radial = new ArcRay(radial, axis.Unit()).At(axis.Length*.005);
        var pos = _pivot*Math.Cos(angle)+radial*Math.Sin(angle);
        var displacement = pos.Reject(_horizon.P);
        var transported = displacement.Length > 1e-12
            ? _horizon.Travel(displacement.Unit(), _horizon.P.AngleTo(pos))
            : _horizon;
        Aim(transported, _pivot.Reject(pos).Unit());
        UpdateDistance();
    }

    private void Pan(Vector2 delta)
    {
        var move = _view.Right * -delta.X + _view.Up * delta.Y;
        if (move.Length < 1e-9) return;
        var axis = move.Unit();
        double angle = move.Length * _pivotDist / (_bake!.Geometry.Radius * 400);
        var ray = new ArcRay(_view.P, axis);
        _pivot += (ray.At(angle)-ray.P)*_pivot.Dot(ray.P) + (ray.Tangent(angle)-axis)*_pivot.Dot(axis);
        var transported = _horizon.Travel(axis, angle);
        Aim(transported, -transported.Pitch(_pitch).Back);
    }
    private double _time;
    private bool _needsBake;

    public GlomeEditorSession(GlomeEditorPlugin plugin)
    {
        this.plugin = plugin;
        panel.Name = "GlomeMainScreen";
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(panel);
        var views = new HFlowContainer();
        panel.AddChild(views);
        views.AddChild(spaces);
        spaces.ItemSelected += index => SelectSpace(spaceNodes[(int)index]);
        mode.ItemSelected += _ => RequestBake();
        GlomeEditorViews.Changed += RefreshViews;
        views.AddChild(mode);
        _image = new TextureRect
        {
            Name = "Preview",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            TooltipText = "",
            CustomMinimumSize = new(0, 135)
        };
        panel.AddChild(_image);
        _image.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _image.FocusMode = Control.FocusModeEnum.Click;
        _image.GuiInput += Look;
        _image.Connect(Control.SignalName.FocusExited, new Callable(plugin, nameof(plugin.PreviewStopFly)));
        _image.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.Draw += DrawOverlay;
        EditorInterface.Singleton.GetSelection().Connect("selection_changed", new Callable(plugin, nameof(plugin.PreviewSelectionChanged)));
        _status = new Label { ClipText = true };
        void Button(Container parent, string title, Action action)
        {
            var button = new Button { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
            button.Pressed += action;
            parent.AddChild(button);

        }
        Button(views, "Reset View", Spawn);
        Button(views, "Focus (F)", FocusSelected);
        Button(views, "From 3D", () =>
        {
            var camera = EditorInterface.Singleton.GetEditorViewport3D().GetCamera3D();
            if (_bake == null || camera == null) return;
            _view = FollowCamera(camera);
            VirtualPivot();
        });
        Button(views, "Flip Antipode (180°)", () =>
        {
            if (_bake == null) return;
            var opposite = _horizon.Travel(_horizon.Back, Math.PI);
            var gaze = _pivot.Reject(opposite.P);
            Aim(opposite, gaze.Length > 1e-9 ? gaze.Unit() : -opposite.Back);
            UpdateDistance();
        });
        Button(views, "Rebake", RequestBake);
        AddBookmarks(views);
        panel.AddChild(_status);
        SelectionChanged();
        plugin.GetUndoRedo().Connect("version_changed", new Callable(plugin, nameof(plugin.PreviewRequestBake)));
        RenderingServer.Singleton.Connect("frame_post_draw", new Callable(plugin, nameof(plugin.PreviewFramePresented)));
        plugin.Connect("scene_changed", new Callable(plugin, nameof(plugin.PreviewSceneSwitched)));
        SceneSwitched(plugin.GetTree().EditedSceneRoot);
    }

    internal void SceneSwitched(Node? root)
    {
        if (_space != null) _space.BakeInvalidated -= MarkDirty;
        ReleaseScene();
        StopFly();
        sceneKey = root?.SceneFilePath ?? "";
        marks = new();
        chosen = -1;
        _space = null;
        RefreshSpaces(root);
    }

    private static IEnumerable<S3Space3D> FindSpaces(Node? root)
    {
        if (root == null || root is Viewport) yield break;
        if (root is S3Space3D space) yield return space;
        foreach (var child in root.GetChildren())
            foreach (var nested in FindSpaces(child)) yield return nested;
    }

    private void RefreshSpaces(Node? root)
    {
        spaceNodes = FindSpaces(root).ToArray();
        spaces.Clear();
        foreach (var space in spaceNodes) spaces.AddItem(root!.GetPathTo(space).ToString());
        spaces.Visible = spaceNodes.Length > 1;
        int index = Array.IndexOf(spaceNodes, _space);
        if (index < 0) SelectSpace(spaceNodes.FirstOrDefault());
        else spaces.Selected = index;
    }

    private void SelectSpace(S3Space3D? space)
    {
        if (_space != null) _space.BakeInvalidated -= MarkDirty;
        ReleaseScene();
        CancelDrag();
        _space = space;
        if (space != null) space.BakeInvalidated += MarkDirty;
        SelectionChanged();
        RefreshViews();
        Message(space != null ? "Preparing preview…" : "Open a scene containing an S3Space3D.");
    }

    private void RefreshViews()
    {
        string id = mode.Selected >= 0 ? providers[mode.Selected].Id : "";
        providers = _space == null ? [] : GlomeEditorViews.For(_space);
        mode.Clear();
        foreach (var provider in providers) mode.AddItem(provider.DisplayName);
        if (providers.Length > 0) SelectView(id);
        CancelDrag();
        _needsBake = _space != null;
    }

    private void SelectView(string id)
    {
        int index = Array.FindIndex(providers, p => p.Id == id);
        mode.Selected = Math.Max(0, index);
        _needsBake = true;
    }

    private void MarkDirty() => _needsBake = true;

    internal void RequestBake()
    {
        RefreshSpaces(plugin.GetTree().EditedSceneRoot);
        CancelDrag();
        _needsBake = _space != null;
    }

    private void Bake()
    {
        dirty = true;
        _needsBake = false;
        if (RenderingServer.GetRenderingDevice() is null) { Message("Vulkan rendering is required."); return; }
        bool initial = _bake == null;
        var data = Provider.Bake(_space!);
        _charts = GlomeChart.Descendants(_space!).OfType<S3Anchor3D>().Where(a => a.EditorChartRadius > 0).ToArray();
        _image.Texture = null;
        _renderer?.Dispose();
        outputSize = Provider.Resolution(TargetSize());
        _renderer = new(data.Buffers, outputSize, data.Program);
        _image.Texture = _renderer.Output;
        _image.TextureFilter = Provider.DisplayFilter;
        viewData = data;
        _bake = data.Snapshot;
        lens = Math.Clamp(_space!.CurrentCamera()?.Fov ?? (float)GlomeComputeRenderer.VerticalFov, 1, 179);
        if (initial) Spawn();
        if (pending.Remove(sceneKey, out var saved)) Restore(saved);
        Message("");
    }

    private S3Frame FollowCamera(Camera3D camera)
    {
        var world = camera.GlobalTransform;
        var flat = ChartCamera.FromUnfolded(_space!.GlobalTransform.AffineInverse() * world, _bake!.Geometry.Radius);
        foreach (var anchor in _charts)
        {
            if (!GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree()) continue;
            Transform3D local = anchor.GlobalTransform.AffineInverse() * world;
            float weight = 1 - Mathf.SmoothStep(anchor.EditorChartRadius, anchor.EditorChartRadius + 5, local.Origin.Length());
            if (weight <= 0) continue;
            var (frame, pose) = anchor.ChartPose(_bake.Geometry.Radius);
            flat = ChartCamera.Blend(flat, ChartCamera.FromTangent(frame, pose * local, _bake.Geometry.Radius), weight);
        }
        return flat;
    }

    public void Process(double delta)
    {

        if (_space is not null && (!GodotObject.IsInstanceValid(_space) || !_space.IsInsideTree()))
            SceneSwitched(plugin.GetTree().EditedSceneRoot);
        if (_space is null || !_image.IsVisibleInTree()) return;
        try
        {
            _time += delta;
            var elapsed = delta;
            if (_needsBake || (_bake is not null && _bake.Geometry.Radius != _space.Radius)) Bake();
            if (_renderer is null || _bake is null) return;
            if (_renderer.Notices.TryDequeue(out var notice)) throw new InvalidOperationException(notice.Message);
            if (flying)
            {
                int Axis(Key pos, Key neg) => (Input.IsPhysicalKeyPressed(pos) ? 1 : 0) - (Input.IsPhysicalKeyPressed(neg) ? 1 : 0);
                var direction = _view.Local(Axis(Key.D, Key.A), Axis(Key.E, Key.Q), Axis(Key.S, Key.W));
                if (direction.Length > 0)
                {
                    var transported = _horizon.Travel(direction.Unit(), speed * elapsed / _bake.Geometry.Radius *
                        (Input.IsKeyPressed(Key.Shift) ? 2.5 : 1));
                    Aim(transported, -transported.Pitch(_pitch).Back);
                }
            }
            UpdateDistance();
            var view = _view;
            var moving = view != submitted;
            UpdateResolution(delta);
            if (!dirty && !moving && !Provider.Animated) return;
            dirty = false;
            submitted = view;
            Message($"{(Antipodal ? "Antipode | Press F to Focus | " : "")}{selected?.Name.ToString() ?? "—"} | {_pivotDist:0.00} m | {_pivotDist / _bake.Geometry.Radius * 180 / Math.PI:0.0}° | {speed:0} m/s | RMB + WASD/QE");
            int pipeline = viewData!.PrepareFrame?.Invoke(_renderer, view, (float)_time) ?? 0;
            _renderer.Draw(view, _bake.Geometry.Radius, _bake.Geometry.Nodes.Length, (float)_time,
                pipeline: pipeline, verticalFov: lens);
            awaitingPresentation = (_bake, view, outputSize, lens);
            _image.QueueRedraw();
        }
        catch (Exception e)
        {
            ReleaseScene();
            GD.PushError(e.ToString());
            Message($"Preview unavailable. {e.Message}");
        }
    }

    internal void FramePresented()
    {
        if (awaitingPresentation is not { } frame) return;
        awaitingPresentation = null;
        (displayedBake, displayed, displayedSize, displayedFov) = frame;
        overlay.QueueRedraw();
    }

    private S3Anchor3D? Selected => EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<S3Anchor3D>()
        .FirstOrDefault(anchor => GlomeChart.SpaceOf(anchor) == _space);
    private void Spawn()
    {
        if (_bake == null) return;
        _view = viewData!.InitialView;
        VirtualPivot();
    }
    private void FocusSelected()
    {
        if (_bake == null || Selected is not { } anchor) return;
        var (frame, transform) = anchor.ChartPose(_bake.Geometry.Radius);
        var pivot = ChartCamera.FromTangent(frame, transform, _bake.Geometry.Radius);
        _pivot = pivot.P;
        var offset = (pivot.Back * 4.5 + pivot.Up * 1.8).Unit();
        _view = pivot.Travel(offset, Math.Sqrt(4.5 * 4.5 + 1.8 * 1.8) / _bake.Geometry.Radius).Pitch(-Math.Atan2(1.8, 4.5));
        UpdateDistance();
    }
    public void Visible(bool visible)
    {
        panel.Visible = visible;
        if (!visible) { StopFly(); CancelDrag(); }
    }
    public void StopFly()
    {
        if (flying) Input.MouseMode = Input.MouseModeEnum.Visible;
        flying = false;
    }
    private void Look(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Escape) { StopFly(); CancelDrag(); }
            if (key.Keycode == Key.F && !key.Echo && !key.CtrlPressed && !key.AltPressed && !key.MetaPressed && !key.ShiftPressed && _image.HasFocus() && _bake != null && Selected != null)
            {
                FocusSelected();
                _image.AcceptEvent();
            }
            return;
        }
        if (DragInput(input)) return;
        if (input is InputEventMouseMotion motion && _bake != null)
        {
            if (flying)
            {
                _horizon = _horizon.Yaw(motion.Relative.X * .005);
                _pitch = Math.Clamp(_pitch-motion.Relative.Y*.005,-Math.PI*89/180,Math.PI*89/180);
            }
            else if ((motion.ButtonMask & MouseButtonMask.Middle) != 0)
            {
                if (motion.ShiftPressed) Pan(motion.Relative);
                else Orbit(motion.Relative);
            }
        }
        if (input is not InputEventMouseButton button || _bake == null) return;
        if (!button.Pressed) { if (button.ButtonIndex == MouseButton.Right) StopFly(); return; }
        var size = ImageSize;
        var uv = (button.Position - (_image.Size - size) / 2) / size;
        if (uv.X < 0 || uv.X >= 1 || uv.Y < 0 || uv.Y >= 1) return;
        _image.GrabFocus();
        if (button.ButtonIndex == MouseButton.Right) { flying = true; Input.MouseMode = Input.MouseModeEnum.Captured; }
        if (!flying && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            double sign = button.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
            double step = Math.Clamp(.12*_pivotDist,.01*_bake.Geometry.Radius,.12*_bake.Geometry.Radius);
            var gaze = -_view.Back;
            var transported = _horizon.Travel(gaze, sign*step/_bake.Geometry.Radius);
            Aim(transported, -transported.Pitch(_pitch).Back);
            if (sign > 0 && step >= _pivotDist) VirtualPivot();
            UpdateDistance();
            _image.AcceptEvent();
            return;
        }
        if (button.ButtonIndex == MouseButton.WheelUp) speed = Math.Min(32, speed * 1.25);
        if (button.ButtonIndex == MouseButton.WheelDown) speed = Math.Max(2, speed / 1.25);
        if (button.ButtonIndex != MouseButton.Left) return;
        var tan = Math.Tan(double.DegreesToRadians(displayedFov) / 2);
        var x = (2 * (Math.Floor(uv.X * displayedSize.X) + .5) / displayedSize.X - 1) * tan * displayedSize.X / displayedSize.Y;
        var y = (1 - 2 * (Math.Floor(uv.Y * displayedSize.Y) + .5) / displayedSize.Y) * tan;
        if (displayedBake?.TriangleOwners is not { } displayedOwners) return;
        var hit = displayedBake.Geometry.Trace(new(displayed.P, displayed.Local(x, y, -1).Unit()));
        if (hit.Kind == HitKind.Triangle || hit.Kind == HitKind.Ground)
            _pivot = new ArcRay(displayed.P, displayed.Local(x,y,-1).Unit()).At(hit.T);
        else VirtualPivot();
        UpdateDistance();
        var selection = EditorInterface.Singleton.GetSelection();
        syncingSelection = true;
        try
        {
            selection.Clear();
            if (hit.Kind == HitKind.Triangle && GodotObject.IsInstanceValid(displayedOwners[hit.Triangle]))
            {
                var picked = displayedOwners[hit.Triangle];
                selection.AddNode(picked);
                EditorInterface.Singleton.InspectObject(picked);
            }
        }
        finally { syncingSelection = false; }
        SelectionChanged();
    }
    private void Message(string message)
    {
        _status.Text = message;
        _status.Visible = message.Length != 0;
    }

    private void ReleaseScene()
    {
        _image.Texture = null;
        _renderer?.Dispose();
        _renderer = null;
        _bake = null;
        displayedBake = null;
        viewData = null;
        awaitingPresentation = null;
        _charts = [];
        dirty = true;
        _needsBake = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (_space != null) _space.BakeInvalidated -= MarkDirty;
        GlomeEditorViews.Changed -= RefreshViews;
        StopFly();
        EditorInterface.Singleton.GetSelection().Disconnect("selection_changed", new Callable(plugin, nameof(plugin.PreviewSelectionChanged)));
        RenderingServer.Singleton.Disconnect("frame_post_draw", new Callable(plugin, nameof(plugin.PreviewFramePresented)));
        plugin.Disconnect("scene_changed", new Callable(plugin, nameof(plugin.PreviewSceneSwitched)));
        plugin.GetUndoRedo().Disconnect("version_changed", new Callable(plugin, nameof(plugin.PreviewRequestBake)));
        ReleaseScene();
        RenderingServer.ForceSync();
        _image.Disconnect(Control.SignalName.FocusExited, new Callable(plugin, nameof(plugin.PreviewStopFly)));
        panel.Free();
    }
}
#endif
