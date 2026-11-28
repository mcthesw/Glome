#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome.Editor;

// Vertices stay in the selection's affine coordinates until draw. Each mesh keeps its own
// chart frame because a nested ParentChart can introduce a relative spin.
internal sealed class GlomeWireframe
{
    internal readonly record struct Edge(S3Frame Chart, Vector3 A, Vector3 B);
    internal List<Edge> Edges { get; } = [];

    internal static GlomeWireframe Capture(Node3D root, S3BakeSnapshot snapshot, bool bounds = false)
    {
        var result = new GlomeWireframe();
        if (snapshot.AuthoringSources is not { } sources) return result;
        double radius = snapshot.Geometry.Radius;
        var (_, rootPose) = GlomeNodeEditing.Pose(root, radius);
        if (!GlomeNodeEditing.Invertible(rootPose.Basis)) return result;
        var inverse = rootPose.AffineInverse();
        if (root is CsgShape3D operand && !operand.IsRootShape())
            Box(GlomeNodeEditing.Pose(root, radius).Frame, Transform3D.Identity, operand.GetAabb());
        var frames = new Dictionary<int, S3Frame>();
        foreach (var (i, placement) in sources.Placements(radius, node => MovesWith(node, root)))
        {
            var (_, frame, pose, box) = placement;
            frames.Add(i, frame);
            if (!bounds) continue;
            Box(frame, inverse * pose, box);
        }
        void Box(S3Frame frame, Transform3D local, Aabb box)
        {
            for (int j = 0; j < 8; j++)
                for (int bit = 1; bit <= 4; bit *= 2)
                    if ((j & bit) == 0) result.Edges.Add(new(frame, local * box.GetEndpoint(j), local * box.GetEndpoint(j | bit)));
        }
        if (bounds || frames.Count == 0) return result;
        int count = 0;
        foreach (int id in sources.TriangleSources) if (frames.ContainsKey(id)) count++;
        int stride = Math.Max(1, (count + 1023) / 1024);
        int visible = 0;
        for (int i = 0; i < sources.TriangleSources.Length; i++)
        {
            if (!frames.TryGetValue(sources.TriangleSources[i], out var frame) || visible++ % stride != 0) continue;
            var triangle = snapshot.Geometry.Triangles[i];
            if (!Local(triangle.A.V, out var a) || !Local(triangle.B.V, out var b) || !Local(triangle.C.V, out var c)) continue;
            result.Edges.Add(new(frame, a, b));
            result.Edges.Add(new(frame, b, c));
            result.Edges.Add(new(frame, c, a));
            bool Local(D4 point, out Vector3 v)
            {
                v = default;
                if (!S3ChartMath.TryInverseCentralProjection(frame, radius, point, out var metres)) return false;
                v = inverse * new Vector3((float)metres.X, (float)metres.Y, (float)metres.Z);
                return true;
            }
        }
        return result;
    }

    internal static bool MovesWith(Node source, Node root)
    {
        for (Node? n = source; n != null; n = n.GetParent())
        {
            if (n == root) return true;
            if (n is S3Space3D or Viewport or S3Anchor3D { ChartPlacement: not S3Anchor3D.PlacementMode.ParentChart }) return false;
        }
        return false;
    }
}
#endif
