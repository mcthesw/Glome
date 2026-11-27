#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed record GlomeInsertion(Node Parent, Node3D Root, List<Node> Owned);

internal static class GlomeScenePlacement
{
    internal static S3Frame Surface(ArcRay ray, RayHit hit, S3BakeSnapshot bake, S3Frame eye)
    {
        double r = bake.Geometry.Radius;
        double t = hit.T;
        if (hit.Kind == HitKind.Miss)
        {
            double floor = ArcRay.FirstRoot(ray.P.Y, ray.D.Y, BakedGeometry.RayStart);
            t = floor < Math.PI / 2 ? floor : Math.Min(4.85/r, .4*Math.PI);
        }
        var transported = eye.Travel(ray.D, t);
        var up = hit.Kind == HitKind.Triangle ? bake.Geometry.Triangles[hit.Triangle].Plane : D4.Up;
        if (hit.Kind == HitKind.Triangle && up.Dot(ray.Tangent(t)) > 0) up = -up;
        up = up.Reject(transported.P);
        if (up.Length < 1e-9) up = transported.Up;
        up = up.Unit();
        var back = transported.Back.Reject(up);
        if (back.Length < 1e-9) back = -D4.Hodge(transported.Right, up, transported.P);
        if (back.Length < 1e-9) back = transported.Up.Reject(up);
        back = back.Unit();
        return new(-D4.Hodge(up, back, transported.P), up, back, transported.P);
    }

    internal static GlomeInsertion? Instance(PackedScene scene, S3Space3D space, S3Anchor3D? parent, S3Frame target)
    {
        var instance = scene.Instantiate(PackedScene.GenEditState.Instance);
        if (instance is not Node3D root || root is S3Space3D)
        {
            instance.Free();
            return null;
        }
        double r = space.Radius;
        if (parent != null)
        {
            var (chart, pose) = parent.ChartPose(r);
            if (!GlomeNodeEditing.TryLocalPoint(chart, pose, r, target.P, out var origin))
            {
                root.Free();
                return null;
            }
            // Differentiate the inverse gnomonic map to align the instance's local +Y to the hit normal.
            double w = target.P.Dot(chart.P);
            Vector3 Axis(D4 axis)
            {
                var tangent = axis - target.P * (axis.Dot(chart.P)/w);
                return new Vector3((float)tangent.Dot(chart.Right), (float)tangent.Dot(chart.Up), (float)tangent.Dot(chart.Back)).Normalized();
            }
            var basis = pose.Basis.Inverse() * new Basis(Axis(target.Right), Axis(target.Up), Axis(target.Back))
                * Basis.FromScale(root.Basis.Scale);
            if (root is S3Anchor3D nested)
            {
                nested.ChartPlacement = S3Anchor3D.PlacementMode.ParentChart;
                nested.RelativeSpin = [];
            }
            root.Transform = new(basis, origin);
            return new(parent, root, [root]);
        }

        if (root is S3Anchor3D anchor)
        {
            if (anchor.ChartPlacement == S3Anchor3D.PlacementMode.ParentChart)
            {
                var offset = anchor.Position;
                anchor.ChartPlacement = S3Anchor3D.PlacementMode.Origin;
                anchor.LocalOffset = offset;
            }
            var delta = GlomeAuthoring.Between(GlomeAuthoring.Pivot(anchor, r), target);
            anchor.RelativeSpin = GlomeAuthoring.DisplacedSpin(anchor, delta, r);
            return new(space, anchor, [anchor]);
        }
        var wrapper = new S3Anchor3D { Name = root.Name + "Anchor", ChartPlacement = S3Anchor3D.PlacementMode.Origin };
        wrapper.AddChild(root);
        wrapper.RelativeSpin = S3ChartMath.Encode(GlomeAuthoring.Between(GlomeAuthoring.Pivot(root, r), target));
        return new(space, wrapper, [wrapper, root]);
    }

    internal static GlomeInsertion Duplicate(Node3D original, Node editedScene)
    {
        // UseInstantiation preserves the PackedScene link; remap only local scene ownership.
        var copy = (Node3D)original.Duplicate();
        List<Node> owned = [copy];
        CopyOwners(original, copy);
        return new(original.GetParent(), copy, owned);

        void CopyOwners(Node source, Node duplicate)
        {
            foreach (var child in source.GetChildren())
            {
                var other = duplicate.GetNodeOrNull(new NodePath(child.Name.ToString()));
                if (other == null) continue;
                if (child.Owner == null) { other.Free(); continue; }
                if (child.Owner == editedScene) owned.Add(other);
                CopyOwners(child, other);
            }
        }
    }

    internal static void Insert(EditorUndoRedoManager undo, EditorSelection selection, Node scene, string title,
        IReadOnlyList<GlomeInsertion> insertions)
    {
        undo.CreateAction(title, customContext: scene);
        undo.AddDoMethod(selection, EditorSelection.MethodName.Clear);
        undo.AddUndoMethod(selection, EditorSelection.MethodName.Clear);
        foreach (var (parent, root, owned) in insertions)
        {
            undo.AddDoMethod(parent, Node.MethodName.AddChild, root, true);
            undo.AddDoReference(root);
            foreach (var child in owned) undo.AddDoProperty(child, Node.PropertyName.Owner, scene);
            undo.AddDoMethod(selection, EditorSelection.MethodName.AddNode, root);
            undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, root);
        }
        foreach (var old in selection.GetSelectedNodes()) undo.AddUndoMethod(selection, EditorSelection.MethodName.AddNode, old);
        undo.CommitAction();
    }
}
#endif
