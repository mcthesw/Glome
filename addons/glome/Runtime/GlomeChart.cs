#nullable enable
using System;
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome;

public static class GlomeChart
{
    // Stop at independent spaces and viewports, including embedded game worlds.
    internal static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is S3Space3D or Viewport) continue;
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    internal static S3Space3D? SpaceOf(Node node)
    {
        for (Node? n = node.GetParent(); n != null && n is not Viewport; n = n.GetParent())
            if (n is S3Space3D space) return space;
        return null;
    }

    internal static (S3Frame Frame, Transform3D Pose) InheritedPose(Node3D node, double radius)
    {
        var (frame, parent) = ParentPose(node.GetParent(), radius);
        return (frame, parent * node.Transform);
    }

    internal static (S3Frame Frame, Transform3D Pose) ParentPose(Node? parent, double radius)
    {
        var pose = Transform3D.Identity;
        for (; parent is not null && parent is not S3Space3D and not Viewport; parent = parent.GetParent())
        {
            if (parent is S3Anchor3D anchor)
            {
                var chart = anchor.ChartPose(radius);
                return (chart.Frame, chart.Pose * pose);
            }
            if (parent is Node3D spatial) pose = spatial.Transform * pose;
        }
        return (S3Frame.Identity, pose);
    }

    internal static S3Frame At(Node3D node, double radius)
    {
        var (frame, pose) = InheritedPose(node, radius);
        return ChartCamera.FromTangent(frame, pose, radius);
    }

    public static void Walk(Node parent, Transform3D transform, Action<Node, Transform3D> visit)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is S3Anchor3D or S3Space3D or Viewport) continue;
            var pose = child is Node3D spatial ? transform * spatial.Transform : transform;
            visit(child, pose);
            Walk(child, pose, visit);
        }
    }
}
