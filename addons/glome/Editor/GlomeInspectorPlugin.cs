#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Glome.Editor;

[Tool]
public partial class GlomeInspectorPlugin : EditorInspectorPlugin
{
    internal EditorUndoRedoManager Undo { private get; init; } = null!;

    private readonly HashSet<Observation> _observations = [];

    internal void DetachAll()
    {
        foreach (var observation in _observations.ToArray()) observation.Detach();
    }

    public override bool _CanHandle(GodotObject obj) => obj is S3Space3D or S3Anchor3D;

    public override bool _ParseProperty(GodotObject obj, Variant.Type type, string name, PropertyHint hint,
        string hintString, PropertyUsageFlags usageFlags, bool wide)
    {
        if (obj is S3Space3D space && name == nameof(space.Radius))
        {
            var box = new VBoxContainer();
            var label = new Label { Name = "RadiusDistances" };
            var radius = new SpinBox
            {
                Name = "ScaleToRadius", MinValue = 0.001, MaxValue = 10000, AllowGreater = true,
                Step = 0.001, Prefix = "Scale to", Suffix = "m", TooltipText = "Target curvature radius"
            };
            var apply = new Button { Text = "Scale Entire Scene" };
            void Scale() => GlomeSceneScale.Apply(space, radius.Value, Undo);
            box.AddChild(label);
            box.AddChild(radius);
            box.AddChild(apply);
            double previous = double.NaN;
            Observe(box, () =>
            {
                if (!IsInstanceValid(space) || previous == space.Radius) return;
                previous = radius.Value = space.Radius;
                label.Text = $"Great Circle Circumference  {Math.Tau * previous:0.00} m\n" +
                    $"Equatorial Distance  {Math.PI / 2 * previous:0.00} m\nAntipodal Distance  {Math.PI * previous:0.00} m";
            }, apply, Scale);
            AddCustomControl(box);
            if (hint == PropertyHint.Range && GlomeRadiusProperty.CreateFor(hintString) is { } slider)
            {
                AddPropertyEditor(name, slider);
                return true;
            }
        }
        return false;
    }

    public override void _ParseBegin(GodotObject obj)
    {
        if (obj is not S3Anchor3D anchor || GlomeChart.SpaceOf(anchor) is not { } space) return;
        var label = new Label { Name = "ChartDistortion", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        Observe(label, () =>
        {
            if (!IsInstanceValid(space) || !IsInstanceValid(anchor)) return;
            var extent = GlomeAuthoring.Measure(anchor, space.Radius);
            double offset = extent.ChartMetres;
            double ratio = offset / space.Radius;
            double compression = extent.Compression(space.Radius);
            label.Text = $"Farthest vertex  {offset:0.00} m\nChart angle  {double.RadiansToDegrees(Math.Atan(ratio)):0.0}°\n" +
                $"Radial compression  {compression:0.0}%" +
                (extent.Distorted(space.Radius) ? "\nA second anchor can reduce distortion." : "");
        });
        AddCustomControl(label);
    }

    private void Observe(Control control, Action update, Button? button = null, Action? press = null)
    {
        var observation = new Observation();
        _observations.Add(observation);
        observation.Attach(this, control, update, button, press);
    }

    // A native object/method Callable keeps its identity while Godot serializes managed scripts.
    [Tool]
    private partial class Observation : RefCounted
    {
        private GlomeInspectorPlugin? _owner;
        private readonly List<(GodotObject Source, StringName Signal, Callable Target)> _connections = [];
        private Action? _update, _press;

        internal void Attach(GlomeInspectorPlugin owner, Control control, Action update, Button? button, Action? press)
        {
            _owner = owner;
            _update = update;
            _press = press;
            Listen(control, Node.SignalName.TreeEntered, MethodName.Refresh);
            Listen(control, Node.SignalName.TreeExiting, MethodName.Detach);
            Listen(owner.Undo, EditorUndoRedoManager.SignalName.VersionChanged, MethodName.Refresh);
            Listen(owner.Undo, EditorUndoRedoManager.SignalName.HistoryChanged, MethodName.Refresh);
            if (button != null) Listen(button, BaseButton.SignalName.Pressed, MethodName.Press);
        }

        private void Listen(GodotObject source, StringName signal, StringName method)
        {
            var target = new Callable(this, method);
            source.Connect(signal, target);
            _connections.Add((source, signal, target));
        }

        private void Refresh() => _update?.Invoke();
        private void Press() => _press?.Invoke();

        internal void Detach()
        {
            foreach (var (source, signal, target) in _connections)
                if (IsInstanceValid(source) && source.IsConnected(signal, target)) source.Disconnect(signal, target);
            _connections.Clear();
            _owner?._observations.Remove(this);
            _owner = null;
            _update = _press = null;
        }
    }

}
#endif
