#nullable enable
#if TOOLS
using System;
using Godot;

namespace Glome.Editor;

[Tool]
public partial class GlomeInspectorPlugin : EditorInspectorPlugin
{
    internal EditorUndoRedoManager Undo { private get; init; } = null!;

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
            apply.Pressed += () => GlomeSceneScale.Apply(space, radius.Value, Undo);
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
            });
            AddCustomControl(box);
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
            double offset = FarthestVertex(anchor, space.Radius);
            double ratio = offset / space.Radius;
            double compression = 100 * ratio * ratio / (1 + ratio * ratio);
            label.Text = $"Farthest vertex  {offset:0.00} m\nChart angle  {double.RadiansToDegrees(Math.Atan(ratio)):0.0}°\n" +
                $"Radial compression  {compression:0.0}%" +
                (ratio >= 1.0 / 3 - 1e-8 ? "\nA second anchor can reduce distortion." : "");
        });
        AddCustomControl(label);
    }

    private void Observe(Control control, Action update)
    {
        control.TreeEntered += () => { update(); Undo.VersionChanged += update; Undo.HistoryChanged += update; };
        control.TreeExiting += () => { Undo.VersionChanged -= update; Undo.HistoryChanged -= update; };
    }

    private static double FarthestVertex(S3Anchor3D anchor, double radius)
    {
        var (_, transform) = anchor.ChartPose(radius);
        double squared = 0;
        GlomeChart.Walk(anchor, transform, (node, pose) =>
        {
            if (node is not MeshInstance3D { Mesh: { } mesh }) return;
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                using var arrays = mesh.SurfaceGetArrays(i);
                foreach (var vertex in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    squared = Math.Max(squared, (pose * vertex).LengthSquared());
            }
        });
        return Math.Sqrt(squared);
    }
}
#endif
