#nullable enable
using System;
using System.Collections.Generic;
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

public static class GlomeBaker
{
    // Visibility governs meshes. Enabled collision shapes remain physical even on hidden nodes.
    public static S3BakeSnapshot Bake(S3Space3D space, double? radiusOverride = null,
        BakeVisibility visibility = BakeVisibility.HonoredUpToRoot, Func<Material?, SurfaceTag>? resolveMaterial = null,
        Action<S3Anchor3D, S3Frame, Transform3D>? onAnchor = null,
        Action<int, CollisionShape3D, S3Frame, Transform3D>? onProxy = null, bool includeOwners = false)
    {
        double radius = radiusOverride ?? space.Radius;
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radiusOverride));
        var triangles = new List<GeodesicTriangle>();
        var lights = new List<GlomeLight>();
        var proxies = new List<CollisionProxy>();
        List<Node3D>? owners = includeOwners ? [] : null;

        var native = resolveMaterial == null ? new NativeMaterialTable() : null;
        var resolve = resolveMaterial ?? native!.Resolve;
        VisitChart(space, null, S3Frame.Identity, Transform3D.Identity, space.Visible);
        var faces = triangles.ToArray();
        var sources = owners?.ToArray();
        var nodes = sources == null ? CapBvhBuilder.Build(faces) : CapBvhBuilder.Build(faces, sources);
        var env = space.Environment ?? new S3Environment();
        var ground = env.GroundMaterial != null && space.EquatorialGroundEnabled && env.EquatorialGroundEnabled
            ? resolve(env.GroundMaterial) : new SurfaceTag(0, 0);
        var environment = env.Capture(space.EquatorialGroundEnabled ? ground.Material : -1);
        bool floor = environment.ModeGroundMaterial.Y >= 0;
        var uv = environment.GroundUvTransform;
        var materials = native?.Finish();
        var geometry = new BakedGeometry(radius, faces, nodes, floor, materials == null ? null : materials.Covers,
            new(uv.X, uv.Y), new(uv.Z, uv.W), (ground.Traversal & TraversalFlags.Cutout) != 0 ? ground.Material : -1);
        return space.Publish(geometry, new(radius, proxies, floor), sources, materials, lights.ToArray(), environment);

        void VisitChart(Node root, S3Anchor3D? owner, S3Frame frame, Transform3D transform, bool visible)
        {
            // Finish a chart's own geometry before its child charts. This keeps triangle and
            // proxy IDs stable even when a procedural mesh is appended after a nested anchor.
            List<(S3Anchor3D Anchor, bool Visible)>? nested = null;
            Walk(root, transform, visible);
            if (nested == null) return;
            foreach (var (anchor, inheritedVisibility) in nested)
            {
                var chart = anchor.ChartPose(radius);
                onAnchor?.Invoke(anchor, chart.Frame, chart.Pose);
                VisitChart(anchor, anchor, chart.Frame, chart.Pose, inheritedVisibility);
            }

            void Walk(Node parent, Transform3D parentPose, bool parentVisible)
            {
                // GetChildren excludes internal editor guides and includes generated nodes with no Owner.
                foreach (var child in parent.GetChildren())
                {
                    if (child is S3Space3D or Viewport) continue;
                    bool childVisible = parentVisible && (child is not Node3D spatial || spatial.Visible);
                    if (child is S3Anchor3D anchor)
                    {
                        (nested ??= []).Add((anchor, childVisible));
                        continue;
                    }

                    var pose = child is Node3D node ? parentPose * node.Transform : parentPose;
                    if (child is MeshInstance3D { Mesh: not null, Visible: true } mesh &&
                        (childVisible || visibility == BakeVisibility.SelfVisible))
                    {
                        Extract(mesh, pose, frame, radius, resolve, triangles);
                        if (owners != null)
                            while (owners.Count < triangles.Count) owners.Add((Node3D?)owner ?? mesh);
                    }
                    if (child is Light3D { Visible: true } light && (childVisible || visibility == BakeVisibility.SelfVisible) &&
                        GlomeLight.FromNode(light, frame, pose, radius) is { } record)
                        lights.Add(record);
                    if (child is CollisionShape3D { Disabled: false } shape &&
                        CollisionShapeAdapter.Create(frame, radius, pose, shape.Shape) is { } proxy)
                    {
                        onProxy?.Invoke(proxies.Count, shape, frame, pose);
                        proxies.Add(proxy);
                    }
                    Walk(child, pose, childVisible);
                }
            }
        }
    }

    private static void Extract(MeshInstance3D instance, Transform3D pose, S3Frame frame, double radius,
                                Func<Material?, SurfaceTag> resolveMaterial, List<GeodesicTriangle> triangles)
    {
        float determinant = pose.Basis.Determinant();
        if (determinant == 0) return;
        var mesh = instance.Mesh;
        Basis normalBasis = pose.Basis.Inverse().Transposed();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var topology = Topology(mesh, surface);
            if (topology is not (Mesh.PrimitiveType.Triangles or Mesh.PrimitiveType.TriangleStrip)) continue;
            bool strip = topology == Mesh.PrimitiveType.TriangleStrip;
            var arrays = mesh.SurfaceGetArrays(surface);
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
            int count = indices.Length == 0 ? vertices.Length : indices.Length;
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
                triangles.Add(new(Vertex(a), Vertex(b), Vertex(c), tag.Material, tag.Surface, tag.Traversal));
            }
        }
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
