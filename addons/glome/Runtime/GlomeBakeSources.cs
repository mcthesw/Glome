#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Glome.Core;
using Godot;

namespace Glome;

public readonly record struct GlomeLightSource(ulong Id, string Path);

internal readonly record struct GlomeMeshPlacement(Node3D Node, S3Frame Frame, Transform3D Pose, Aabb Bounds);
internal readonly record struct SpanWireEdge(Vector3 A, Vector3 B);
internal sealed record SpanPreviewGeometry(SpanWireEdge[] Edges, CollisionShapeAdapter.Input[] Proxies);

/// <summary>Editor-only capture in BVH order. Runtime snapshots omit these node references.</summary>
public sealed record GlomeAuthoringSources(MeshInstance3D[] Meshes, int[] TriangleSources, Light3D[] Lights)
{
    // Mesh indices retain their original meaning. A negative index addresses an
    // evaluated surface (a CSG root or a repeated asset) by its bitwise complement.
    internal GlomeMeshPlacement[] Evaluated { get; init; } = [];
    internal IReadOnlyDictionary<ulong, SpanPreviewGeometry>? SpanTemplates { get; init; }
    public Node3D SourceForTriangle(int triangle)
    {
        int index = TriangleSources[triangle];
        return index >= 0 ? Meshes[index] : Evaluated[~index].Node;
    }

    internal IEnumerable<(int Index, GlomeMeshPlacement Placement)> Placements(double radius, Predicate<Node3D> include)
    {
        for (int i = 0; i < Meshes.Length; i++)
        {
            var mesh = Meshes[i];
            if (!GodotObject.IsInstanceValid(mesh) || !include(mesh)) continue;
            var (frame, pose) = GlomeChart.InheritedPose(mesh, radius);
            yield return (i, new(mesh, frame, pose, mesh.GetAabb()));
        }
        for (int i = 0; i < Evaluated.Length; i++)
            if (GodotObject.IsInstanceValid(Evaluated[i].Node) && include(Evaluated[i].Node)) yield return (~i, Evaluated[i]);
    }

    internal (Node3D Node, CollisionShapeAdapter.Input Proxy)[] Proxies { get; init; } = [];
    internal ulong GroundMaterialId { get; init; }
}

/// <summary>CPU wall time for a single bake. Upload measures resource submission on the render thread.</summary>
public sealed class GlomeBakeTimings(string view)
{
    internal double Extract, Texture, Project, Bvh;
    internal double Pack { get; set; }
    internal static double Since(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    internal void Uploaded(long start) =>
        GD.Print($"[GLOME-BAKE] {view} Extract={Extract:F2} Texture={Texture:F2} Project={Project:F2} BVH={Bvh:F2} Pack={Pack:F2} Upload={Since(start):F2} ms");
}
