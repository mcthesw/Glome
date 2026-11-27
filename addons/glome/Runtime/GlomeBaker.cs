#nullable enable
using System;
using System.Collections.Generic;
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
    Predicate<D4>? GroundMask = null);

public static class GlomeBaker
{
    private sealed record BakeInputs(double Radius, MeshSurface[] Surfaces, CollisionShapeAdapter.Input[] Proxies,
        SurfaceTag Ground, EnvironmentBlock Environment, GlomeMaterials? Materials, GlomeLight[] Lights, bool TrackOwners);

    private sealed record MeshSurface(Vector3[] Vertices, Vector3[] Normals, Vector2[] Uvs, int[] Indices,
        S3Frame Frame, Transform3D Pose, Vector3 UvScale, Vector3 UvOffset, SurfaceTag Tag, bool Strip, int OwnerIndex);

    private sealed record BakeResult(BakedGeometry Geometry, S3CollisionWorld Collision, int[]? OwnerIndices);

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
        return Publish(space, inputs, BakeCore(inputs, options.GroundMask), owners);
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
        var result = await Task.Run(() => BakeCore(inputs, mask), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GodotObject.IsInstanceValid(space) || space.BakeGeneration != generation)
            throw new OperationCanceledException("Space was freed or its bake was superseded.");
        return Publish(space, inputs, result, owners);
    }

    // Visibility governs meshes. Enabled collision shapes remain physical even on hidden nodes.
    private static async ValueTask<(BakeInputs, Node3D[]?)> ExtractInputs(
        S3Space3D space, GlomeBakeOptions options, Func<ValueTask>? yieldIfNeeded)
    {
        double radius = options.RadiusOverride ?? space.Radius;
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(options.RadiusOverride));
        var surfaces = new List<MeshSurface>();
        var lights = new List<GlomeLight>();
        var proxies = new List<CollisionShapeAdapter.Input>();
        List<Node3D>? sources = options.IncludeOwners ? [] : null;

        var native = options.ResolveMaterial == null ? new NativeMaterialTable() : null;
        var resolve = options.ResolveMaterial ?? native!.Resolve;
        await VisitChart(space, null, S3Frame.Identity, Transform3D.Identity, space.Visible);
        var env = space.Environment ?? new S3Environment();
        var ground = env.GroundMaterial != null && space.EquatorialGroundEnabled && env.EquatorialGroundEnabled
            ? resolve(env.GroundMaterial) : new SurfaceTag(0, 0);
        var environment = env.Capture(space.EquatorialGroundEnabled ? ground.Material : -1);
        return (new(radius, surfaces.ToArray(), proxies.ToArray(), ground, environment, native?.Finish(), lights.ToArray(), options.IncludeOwners), sources?.ToArray());

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
                    if (child is MeshInstance3D { Mesh: not null, Visible: true } mesh &&
                        (childVisible || options.Visibility == BakeVisibility.SelfVisible))
                    {
                        ExtractSurfaces(mesh, pose, frame, resolve, surfaces, sources?.Count ?? -1);
                        sources?.Add((Node3D?)owner ?? mesh);
                    }
                    if (child is Light3D { Visible: true } light && (childVisible || options.Visibility == BakeVisibility.SelfVisible) &&
                        GlomeLight.FromNode(light, frame, pose, radius) is { } record)
                        lights.Add(record);
                    if (child is CollisionShape3D { Disabled: false } shape &&
                        CollisionShapeAdapter.Capture(frame, pose, shape.Shape) is { } proxy)
                    {
                        options.OnProxy?.Invoke(proxies.Count, shape, frame, pose);
                        proxies.Add(proxy);
                    }
                    await Walk(child, pose, childVisible);
                }
            }
        }
    }

    private static void ExtractSurfaces(MeshInstance3D instance, Transform3D pose, S3Frame frame,
        Func<Material?, SurfaceTag> resolveMaterial, List<MeshSurface> surfaces, int ownerIndex)
    {
        float determinant = pose.Basis.Determinant();
        if (determinant == 0) return;
        var mesh = instance.Mesh;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var topology = Topology(mesh, surface);
            if (topology is not (Mesh.PrimitiveType.Triangles or Mesh.PrimitiveType.TriangleStrip)) continue;
            bool strip = topology == Mesh.PrimitiveType.TriangleStrip;
            using var arrays = mesh.SurfaceGetArrays(surface);
            if (instance.Skin != null || arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                NativeMaterialTable.Warn(mesh, "skinning");
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var material = instance.GetActiveMaterial(surface);
            var tag = resolveMaterial(material);
            var standard = material as BaseMaterial3D;
            Vector3 uvScale = standard?.Uv1Scale ?? Vector3.One, uvOffset = standard?.Uv1Offset ?? Vector3.Zero;
            surfaces.Add(new(vertices, normals, uvs, indices, frame, pose, uvScale, uvOffset, tag, strip, ownerIndex));
        }
    }

    private static BakeResult BakeCore(BakeInputs inputs, Predicate<D4>? groundMask = null)
    {
        // Extraction has already copied the engine data. Each surface can now be
        // projected independently. Concatenate in authored order to preserve hit and owner IDs.
        var surfaces = inputs.Surfaces;
        var batches = new GeodesicTriangle[surfaces.Length][];
        Parallel.For(0, surfaces.Length, i => batches[i] = Project(surfaces[i], inputs.Radius));
        int count = 0;
        foreach (var batch in batches) count = checked(count + batch.Length);
        var faces = new GeodesicTriangle[count];
        var indices = inputs.TrackOwners ? new int[count] : null;
        int first = 0;
        for (int i = 0; i < batches.Length; i++)
        {
            var batch = batches[i];
            batch.CopyTo(faces, first);
            if (indices != null) Array.Fill(indices, surfaces[i].OwnerIndex, first, batch.Length);
            first += batch.Length;
        }
        var nodes = indices is null ? CapBvhBuilder.Build(faces) : CapBvhBuilder.Build(faces, indices);
        bool floor = inputs.Environment.ModeGroundMaterial.Y >= 0;
        var uv = inputs.Environment.GroundUvTransform;
        var ground = inputs.Ground;
        var materials = inputs.Materials;
        var geometry = new BakedGeometry(inputs.Radius, faces, nodes, floor, materials == null ? null : materials.Covers,
            new(uv.X, uv.Y), new(uv.Z, uv.W), (ground.Traversal & TraversalFlags.Cutout) != 0 ? ground.Material : -1, groundMask);
        var proxies = Array.ConvertAll(inputs.Proxies, proxy => proxy.Bake(inputs.Radius));
        return new(geometry, new(inputs.Radius, proxies, floor), indices);
    }

    private static S3BakeSnapshot Publish(S3Space3D space, BakeInputs inputs, BakeResult result, Node3D[]? sources)
    {
        var owners = result.OwnerIndices is { } indices ? Array.ConvertAll(indices, index => sources![index]) : null;
        return space.Publish(result.Geometry, result.Collision, owners, inputs.Materials, inputs.Lights, inputs.Environment);
    }

    private static GeodesicTriangle[] Project(MeshSurface surface, double radius)
    {
        var (vertices, normals, uvs, indices, frame, pose, uvScale, uvOffset, tag, strip, _) = surface;
        float determinant = pose.Basis.Determinant();
        Basis normalBasis = pose.Basis.Inverse().Transposed();
        int count = indices.Length == 0 ? vertices.Length : indices.Length;
        var triangles = new GeodesicTriangle[strip ? Math.Max(0, count - 2) : count / 3];
        int written = 0;
        for (int i = 0; i + 2 < count; i += strip ? 1 : 3)
        {
            int Index(int n) => indices.Length == 0 ? i + n : indices[i + n];
            int a = Index(0), b = Index(1), c = Index(2);
            if (strip && (i & 1) != 0) (a, b) = (b, a);
            Vector3 face = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
            if (face.LengthSquared() < 1e-13f) continue;
            ChartVertex Vertex(int j)
            {
                Vector3 p = pose * vertices[j];
                Vector3 n = normalBasis * (normals.Length == vertices.Length ? normals[j] : face);
                if (n.IsZeroApprox()) n = normalBasis * face;
                Vector2 uv = uvs.Length == vertices.Length ? uvs[j] : new(p.X, p.Z);
                return ChartVertex.Project(frame, radius, frame.Local(p.X, p.Y, p.Z), frame.Local(n.X, n.Y, n.Z),
                    new(uv.X * uvScale.X + uvOffset.X, uv.Y * uvScale.Y + uvOffset.Y));
            }
            // A reflected transform reverses chart winding. Godot keeps the authored front face.
            if (determinant < 0 && (tag.Traversal & TraversalFlags.CullMask) != TraversalFlags.TwoSided) (b, c) = (c, b);
            triangles[written++] = new(Vertex(a), Vertex(b), Vertex(c), tag.Material, tag.Surface, tag.Traversal);
        }
        if (written != triangles.Length) Array.Resize(ref triangles, written);
        return triangles;
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
