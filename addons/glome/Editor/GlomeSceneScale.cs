#nullable enable
#if TOOLS
using Godot;

namespace Glome.Editor;

internal static class GlomeSceneScale
{
    internal static void Apply(S3Space3D space, double radius, EditorUndoRedoManager undo)
    {
        radius = space.EffectiveRadius(radius);
        if (!double.IsFinite(radius) || radius <= 0 || radius == space.Radius) return;
        double k = radius / space.Radius;
        undo.CreateAction("Scale Entire Scene", customContext: space);
        Change(space, nameof(space.Radius), radius);
        Visit(space, inheritedScale: false);
        undo.AddDoMethod(space, nameof(space.RequestRebake));
        undo.AddUndoMethod(space, nameof(space.RequestRebake));
        undo.CommitAction();

        void Change(GodotObject obj, StringName property, Variant value)
        {
            undo.AddDoProperty(obj, property, value);
            undo.AddUndoProperty(obj, property, obj.Get(property));
        }

        void Metric(GodotObject obj, StringName property) => Change(obj, property, obj.Get(property).AsDouble() * k);

        void Visit(Node parent, bool inheritedScale)
        {
            foreach (var child in parent.GetChildren())
            {
                if (child is S3Space3D or Viewport) continue;
                bool scaled = inheritedScale;
                if (child is S3Anchor3D anchor)
                {
                    Metric(anchor, nameof(anchor.ArcMetres));
                    Metric(anchor, nameof(anchor.EditorChartRadius));
                    if (anchor.ChartPlacement != S3Anchor3D.PlacementMode.ParentChart)
                    {
                        // Absolute anchors reset the bake transform even inside another scaled chart.
                        Change(anchor, nameof(anchor.LocalOffset), anchor.LocalOffset * (float)k);
                        scaled = false;
                    }
                }
                if (child is Node3D spatial && !scaled)
                {
                    Change(spatial, Node3D.PropertyName.Transform, spatial.Transform.Scaled(Vector3.One * (float)k));
                    scaled = true;
                }
                if (child is OmniLight3D light) Metric(light, OmniLight3D.PropertyName.OmniRange);
                if (child is S3CharacterController controller)
                {
                    Metric(controller, nameof(controller.BodyRadius));
                    Metric(controller, nameof(controller.BodyHeight));
                    Metric(controller, nameof(controller.EyeHeight));
                    Metric(controller, nameof(controller.WalkSpeed));
                    Metric(controller, nameof(controller.SprintSpeed));
                    Metric(controller, nameof(controller.FlightSpeed));
                }
                Visit(child, scaled);
            }
        }
    }
}
#endif
