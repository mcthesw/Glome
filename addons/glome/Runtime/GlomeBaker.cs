#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Glome.Core;
using Godot;

namespace Glome;

public enum BakeVisibility
{
    // Includes the space itself, independent of ancestors outside its chart hierarchy.
    HonoredUpToRoot,
    // Useful when a space is hidden to suppress native rasterization of its source meshes.
    SelfVisible
}
public readonly record struct SurfaceTag(int Material, int Surface, TraversalFlags Traversal = TraversalFlags.TwoSided);

/// <summary>Extraction callbacks run on the Godot thread. GroundMask must be a pure value predicate.</summary>
public sealed record GlomeBakeOptions(
    double? RadiusOverride = null,
    BakeVisibility Visibility = BakeVisibility.HonoredUpToRoot,
    Func<Material?, SurfaceTag>? ResolveMaterial = null,
    Action<S3Anchor3D, S3Frame, Transform3D>? OnAnchor = null,
    Action<int, CollisionShape3D, S3Frame, Transform3D>? OnProxy = null,
    bool IncludeOwners = false,
    Predicate<D4>? GroundMask = null,
    bool CaptureAuthoringSources = false)
{
    public GlomeBakeTimings? Timings { get; init; }
}

public static partial class GlomeBaker
{
    private sealed record BakeInputs(double Radius, MeshSurface[] Surfaces, CollisionShapeAdapter.Input[] Proxies,
        SurfaceTag Ground, EnvironmentBlock Environment, GlomeMaterials? Materials, GlomeLight[] Lights, bool TrackOwners);

    private sealed record MeshSurface(Vector3[] Vertices, Vector3[] Normals, Vector2[] Uvs, int[] Indices,
        S3Frame Frame, Transform3D Pose, Vector3 UvScale, Vector3 UvOffset, SurfaceTag Tag, bool Strip, int OwnerIndex);

    private sealed record BakeResult(BakedGeometry Geometry, S3CollisionWorld Collision, int[]? OwnerIndices);

    private sealed class Sources(GlomeBakeOptions options)
    {
        public readonly List<Node3D>? Owners = options.IncludeOwners ? [] : null;
        public readonly List<MeshInstance3D>? Meshes = options.CaptureAuthoringSources ? [] : null;
        public readonly List<Light3D>? Lights = options.CaptureAuthoringSources ? [] : null;
        public readonly List<(Node3D, CollisionShapeAdapter.Input)>? Proxies = options.CaptureAuthoringSources ? [] : null;
        public readonly List<GlomeMeshPlacement>? Evaluated = options.CaptureAuthoringSources || options.IncludeOwners ? [] : null;
        public Dictionary<ulong, SpanPreviewGeometry>? SpanTemplates;
        public int AddEvaluated(Node3D node, S3Frame frame, Transform3D pose, Aabb bounds)
        {
            int id = ~(Evaluated?.Count ?? 0);
            Evaluated?.Add(new(node, frame, pose, bounds));
            return id;
        }
        public readonly List<GlomeLightSource> LightSources = [];
        public ulong GroundMaterialId;
    }

    public static S3BakeSnapshot Bake(S3Space3D space, double? radiusOverride = null,
        BakeVisibility visibility = BakeVisibility.HonoredUpToRoot, Func<Material?, SurfaceTag>? resolveMaterial = null,
        Action<S3Anchor3D, S3Frame, Transform3D>? onAnchor = null,
        Action<int, CollisionShape3D, S3Frame, Transform3D>? onProxy = null, bool includeOwners = false)
        => Bake(space, new GlomeBakeOptions(radiusOverride, visibility, resolveMaterial, onAnchor, onProxy, includeOwners));

    public static S3BakeSnapshot Bake(S3Space3D space, GlomeBakeOptions? options)
    {
        options ??= new();
        space.BeginBake();
        var (inputs, owners) = ExtractInputs(space, options, null).GetAwaiter().GetResult();
        return Publish(space, inputs, BakeCore(inputs, options.GroundMask, options.Timings), owners);
    }

    /// <summary>Call on the Godot main thread. Only captured values are processed on the worker.</summary>
    public static Task<S3BakeSnapshot> BakeAsync(S3Space3D space, GlomeBakeOptions? options = null,
        CancellationToken cancellationToken = default) => BakeAsync(space, options, cancellationToken, null);

    // Only a private preparation scene may yield during extraction. Public bakes snapshot in one traversal.
    internal static async Task<S3BakeSnapshot> BakeAsync(S3Space3D space, GlomeBakeOptions? options,
        CancellationToken cancellationToken, Func<ValueTask>? yieldIfNeeded)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        long generation = space.BeginBake();
        var (inputs, owners) = await ExtractInputs(space, options, yieldIfNeeded);
        var mask = options.GroundMask;
        var timings = options.Timings;
        var result = await Task.Run(() => BakeCore(inputs, mask, timings, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GodotObject.IsInstanceValid(space) || space.BakeGeneration != generation)
            throw new OperationCanceledException("Space was freed or its bake was superseded.");
        return Publish(space, inputs, result, owners);
    }

    // Visibility governs meshes. Enabled collision shapes remain physical even on hidden nodes.
    private static async ValueTask<(BakeInputs, Sources)> ExtractInputs(
        S3Space3D space, GlomeBakeOptions options, Func<ValueTask>? yieldIfNeeded)
    {
        double radius = options.RadiusOverride ?? space.Radius;
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(options.RadiusOverride));
        var surfaces = new List<MeshSurface>();
        var lights = new List<GlomeLight>();
        var proxies = new List<CollisionShapeAdapter.Input>();
        var sources = new Sources(options);
        var start = Stopwatch.GetTimestamp();
        double priorTexture = options.Timings?.Texture ?? 0;

        var native = options.ResolveMaterial == null ? new NativeMaterialTable() : null;
        var resolve = options.ResolveMaterial ?? native!.Resolve;
        Dictionary<PackedScene, SpanTemplate>? spanTemplates = null;
        await VisitChart(space, null, S3Frame.Identity, Transform3D.Identity, space.Visible);
        var env = space.Environment ?? new S3Environment();
        if (sources.Meshes != null) sources.GroundMaterialId = env.GroundMaterial?.GetInstanceId() ?? 0;
        var ground = env.GroundMaterial != null && space.EquatorialGroundEnabled && env.EquatorialGroundEnabled
            ? resolve(env.GroundMaterial) : new SurfaceTag(0, 0);
        var environment = env.Capture(space.EquatorialGroundEnabled ? ground.Material : -1);
        if (options.Timings is { } timing)
            timing.Extract += GlomeBakeTimings.Since(start) - (timing.Texture - priorTexture);
        start = Stopwatch.GetTimestamp();
        var materials = native?.Finish();
        if (native != null && options.Timings is { } texture) texture.Texture += GlomeBakeTimings.Since(start);
        return (new(radius, surfaces.ToArray(), proxies.ToArray(), ground, environment, materials,
            lights.ToArray(), options.IncludeOwners || options.CaptureAuthoringSources), sources);

        async ValueTask VisitChart(Node root, S3Anchor3D? owner, S3Frame frame, Transform3D transform, bool visible)
        {
            // Finish a chart's own geometry before its child charts. This keeps triangle and
            // proxy IDs stable even when a procedural mesh is appended after a nested anchor.
            List<(S3Anchor3D Anchor, bool Visible)>? nested = null;
            await Walk(root, transform, visible);
            if (nested == null) return;
            foreach (var (anchor, inheritedVisibility) in nested)
            {
                var chart = anchor.ChartPose(radius);
                options.OnAnchor?.Invoke(anchor, chart.Frame, chart.Pose);
                if (anchor is S3GeodesicSpan3D span)
                    ExtractSpan(span, radius, inheritedVisibility || options.Visibility == BakeVisibility.SelfVisible,
                        options.Visibility, resolve, spanTemplates ??= [], surfaces, proxies, sources);
                await VisitChart(anchor, anchor, chart.Frame, chart.Pose, inheritedVisibility);
            }

            async ValueTask Walk(Node parent, Transform3D parentPose, bool parentVisible)
            {
                // GetChildren excludes internal editor guides and includes generated nodes with no Owner.
                foreach (var child in parent.GetChildren())
                {
                    if (yieldIfNeeded != null) await yieldIfNeeded();
                    if (child is S3Space3D or Viewport) continue;
                    bool childVisible = parentVisible && (child is not Node3D spatial || spatial.Visible);
                    if (child is S3Anchor3D anchor)
                    {
                        (nested ??= []).Add((anchor, childVisible));
                        continue;
                    }

                    var pose = child is Node3D node ? parentPose * node.Transform : parentPose;
                    bool render = child is Node3D { Visible: true } && (childVisible || options.Visibility == BakeVisibility.SelfVisible);
                    if ((render || child is CsgShape3D { UseCollision: true }) && GlomeMeshSource.Read(child) is { } source)
                    {
                        if (render)
                        {
                            int index;
                            if (source.Node is MeshInstance3D mesh)
                            {
                                index = sources.Meshes?.Count ?? sources.Owners?.Count ?? -1;
                                sources.Owners?.Add((Node3D?)owner ?? mesh);
                                sources.Meshes?.Add(mesh);
                            }
                            else index = sources.AddEvaluated(source.Node, frame, pose, source.Mesh.GetAabb());
                            ExtractSurfaces(source, pose, frame, resolve, surfaces, index);
                        }
                        if (source.CsgProxy(frame, pose) is { } solid)
                        {
                            proxies.Add(solid);
                            sources.Proxies?.Add((source.Node, solid));
                        }
                    }
                    if (child is Light3D { Visible: true } light && (childVisible || options.Visibility == BakeVisibility.SelfVisible) &&
                        GlomeLight.FromNode(light, frame, pose, radius) is { } record)
                    {
                        lights.Add(record);
                        sources.LightSources.Add(new(light.GetInstanceId(), space.GetPathTo(light).ToString()));
                        sources.Lights?.Add(light);
                    }
                    if (child is CollisionShape3D { Disabled: false } shape &&
                        CollisionShapeAdapter.Capture(frame, pose, shape.Shape) is { } proxy)
                    {
                        options.OnProxy?.Invoke(proxies.Count, shape, frame, pose);
                        proxies.Add(proxy);
                        sources.Proxies?.Add((shape, proxy));
                    }
                    await Walk(child, pose, childVisible);
                }
            }
        }
    }

    private static void ExtractSurfaces(GlomeMeshSource source, Transform3D pose, S3Frame frame,
        Func<Material?, SurfaceTag> resolveMaterial, List<MeshSurface> surfaces, int ownerIndex)
    {
        float determinant = pose.Basis.Determinant();
        if (determinant == 0) return;
        var mesh = source.Mesh;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var topology = Topology(mesh, surface);
            if (topology is not (Mesh.PrimitiveType.Triangles or Mesh.PrimitiveType.TriangleStrip)) continue;
            bool strip = topology == Mesh.PrimitiveType.TriangleStrip;
            using var arrays = mesh.SurfaceGetArrays(surface);
            if (source.Node is MeshInstance3D { Skin: not null } || arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                NativeMaterialTable.Warn(mesh, "skinning");
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var material = source.MaterialAt(surface);
            var tag = resolveMaterial(material);
            var standard = material as BaseMaterial3D;
            Vector3 uvScale = standard?.Uv1Scale ?? Vector3.One, uvOffset = standard?.Uv1Offset ?? Vector3.Zero;
            surfaces.Add(new(vertices, normals, uvs, indices, frame, pose, uvScale, uvOffset, tag, strip, ownerIndex));
        }
    }

    private static BakeResult BakeCore(BakeInputs inputs, Predicate<D4>? groundMask, GlomeBakeTimings? timing, CancellationToken cancellation = default)
    {
        // Partition by triangle count as well as by surface: a single authored mesh
        // can otherwise leave only one worker busy at the end of a large bake.
        long start = Stopwatch.GetTimestamp();
        const int batchSize = 512;
        var batches = new List<(MeshSurface Surface, int First, int Count, int Destination)>();
        int capacity = 0;
        foreach (var surface in inputs.Surfaces)
        {
            int vertices = surface.Indices.Length == 0 ? surface.Vertices.Length : surface.Indices.Length;
            int count = surface.Strip ? Math.Max(0, vertices - 2) : vertices / 3;
            for (int first = 0; first < count; first += batchSize)
                batches.Add((surface, first, Math.Min(batchSize, count - first), checked(capacity + first)));
            capacity = checked(capacity + count);
        }
        var faces = new GeodesicTriangle[capacity];
        var written = new int[batches.Count];
        Parallel.For(0, batches.Count, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            var batch = batches[i];
            written[i] = Project(batch.Surface, inputs.Radius, batch.First, batch.Count, faces, batch.Destination);
        });
        // Compact rejected degenerates in authored order before assigning owner IDs.
        int total = 0;
        var indices = inputs.TrackOwners ? new int[capacity] : null;
        for (int i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            if (total != batch.Destination) Array.Copy(faces, batch.Destination, faces, total, written[i]);
            if (indices != null) Array.Fill(indices, batch.Surface.OwnerIndex, total, written[i]);
            total += written[i];
        }
        if (total != capacity)
        {
            Array.Resize(ref faces, total);
            if (indices != null) Array.Resize(ref indices, total);
        }
        cancellation.ThrowIfCancellationRequested();
        if (timing != null) timing.Project = GlomeBakeTimings.Since(start);
        start = Stopwatch.GetTimestamp();
        var nodes = indices is null ? CapBvhBuilder.Build(faces) : CapBvhBuilder.Build(faces, indices);
        if (timing != null) timing.Bvh = GlomeBakeTimings.Since(start);
        bool floor = inputs.Environment.ModeGroundMaterial.Y >= 0;
        var uv = inputs.Environment.GroundUvTransform;
        var ground = inputs.Ground;
        var materials = inputs.Materials;
        var geometry = new BakedGeometry(inputs.Radius, faces, nodes, floor, materials == null ? null : materials.Covers,
            new(uv.X, uv.Y), new(uv.Z, uv.W), (ground.Traversal & TraversalFlags.Cutout) != 0 ? ground.Material : -1, groundMask);
        var proxies = Array.ConvertAll(inputs.Proxies, proxy => proxy.Bake(inputs.Radius));
        return new(geometry, new(inputs.Radius, proxies, floor), indices);
    }

    private static S3BakeSnapshot Publish(S3Space3D space, BakeInputs inputs, BakeResult result, Sources sources)
    {
        var owners = sources.Owners == null ? null : Array.ConvertAll(result.OwnerIndices!,
            i => i < 0 ? sources.Evaluated![~i].Node : sources.Owners[i]);
        var authoring = sources.Meshes == null ? null : new GlomeAuthoringSources(sources.Meshes.ToArray(),
            result.OwnerIndices!, sources.Lights!.ToArray()) { Evaluated = sources.Evaluated!.ToArray(),
                SpanTemplates = sources.SpanTemplates, Proxies = sources.Proxies!.ToArray(), GroundMaterialId = sources.GroundMaterialId };
        return space.Publish(result.Geometry, result.Collision, owners, inputs.Materials, inputs.Lights,
            inputs.Environment, sources.LightSources.ToArray(), authoring);
    }

    private static int Project(MeshSurface surface, double radius, int first, int count,
        GeodesicTriangle[] triangles, int destination)
    {
        var (vertices, normals, uvs, indices, frame, pose, uvScale, uvOffset, tag, strip, _) = surface;
        float determinant = pose.Basis.Determinant();
        Basis normalBasis = pose.Basis.Inverse().Transposed();
        bool vertexNormals = normals.Length == vertices.Length;
        D4 LiftNormal(Vector3 authored, Vector3 face)
        {
            var n = normalBasis * authored;
            if (n.IsZeroApprox()) n = normalBasis * face;
            return frame.Local(n.X, n.Y, n.Z);
        }
        ChartVertex Vertex(int j, D4 normal)
        {
            Vector3 p = pose * vertices[j];
            Vector2 uv = uvs.Length == vertices.Length ? uvs[j] : new(p.X, p.Z);
            return ChartVertex.Project(frame, radius, frame.Local(p.X, p.Y, p.Z), normal,
                new(uv.X * uvScale.X + uvOffset.X, uv.Y * uvScale.Y + uvOffset.Y));
        }
        int written = destination;
        for (int faceIndex = first; faceIndex < first + count; faceIndex++)
        {
            int i = strip ? faceIndex : faceIndex * 3;
            int a = i, b = i + 1, c = i + 2;
            if (indices.Length != 0) (a, b, c) = (indices[a], indices[b], indices[c]);
            if (strip && (i & 1) != 0) (a, b) = (b, a);
            Vector3 face = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
            if (face.LengthSquared() < 1e-13f) continue;
            // A reflected transform reverses chart winding. Godot keeps the authored front face.
            if (determinant < 0 && (tag.Traversal & TraversalFlags.CullMask) != TraversalFlags.TwoSided) (b, c) = (c, b);
            var na = vertexNormals ? normals[a] : face;
            var normalA = LiftNormal(na, face);
            var normalB = !vertexNormals || na == normals[b] ? normalA : LiftNormal(normals[b], face);
            var normalC = !vertexNormals || na == normals[c] ? normalA : LiftNormal(normals[c], face);
            if (GeodesicTriangle.TryCreate(Vertex(a, normalA), Vertex(b, normalB), Vertex(c, normalC),
                    tag.Material, tag.Surface, tag.Traversal) is { } triangle)
                triangles[written++] = triangle;
        }
        return written - destination;
    }

    private static Mesh.PrimitiveType Topology(Mesh mesh, int surface)
    {
        if (mesh is ArrayMesh array) return array.SurfaceGetPrimitiveType(surface);
        if (mesh is PointMesh) return Mesh.PrimitiveType.Points;
        if (mesh is PrimitiveMesh) return Mesh.PrimitiveType.Triangles;
        using var tool = new SurfaceTool();
        tool.CreateFrom(mesh, surface);
        return tool.GetPrimitiveType();
    }
}
