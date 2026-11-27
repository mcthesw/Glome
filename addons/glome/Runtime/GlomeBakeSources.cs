#nullable enable
using System;
using System.Diagnostics;
using Glome.Core;
using Godot;

namespace Glome;

public readonly record struct GlomeLightSource(ulong Id, string Path);

/// <summary>Editor-only capture in BVH order. Runtime snapshots omit these node references.</summary>
public sealed record GlomeAuthoringSources(MeshInstance3D[] Meshes, int[] TriangleSources, Light3D[] Lights)
{
    internal (CollisionShape3D Node, CollisionShapeAdapter.Input Proxy)[] Proxies { get; init; } = [];
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
