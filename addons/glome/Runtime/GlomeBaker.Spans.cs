#nullable enable
using System;
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome;

public static partial class GlomeBaker
{
    private sealed record SpanMesh(Transform3D Pose, Aabb Bounds, MeshSurface[] Surfaces);
    private sealed record SpanTemplate(List<SpanMesh> Meshes, List<CollisionShapeAdapter.Input> Proxies);

    private static void ExtractSpan(S3GeodesicSpan3D span, double radius, bool visible, BakeVisibility visibility,
        Func<Material?, SurfaceTag> resolve, Dictionary<PackedScene, SpanTemplate> cache,
        List<MeshSurface> surfaces, List<CollisionShapeAdapter.Input> proxies, Sources sources)
    {
        // Capture a PackedScene once per bake. Detached instances neither pollute the
        // editable scene nor start gameplay; only copied vertex and proxy data survive.
        void Prepare(PackedScene? asset)
        {
            if (asset == null || cache.ContainsKey(asset)) return;
            var template = ReadSpanTemplate(asset, visibility, resolve);
            cache.Add(asset, template);
            if (sources.Meshes != null)
                (sources.SpanTemplates ??= []).Add(asset.GetInstanceId(), new(WireEdges(template), template.Proxies.ToArray()));
        }
        Prepare(span.SegmentScene);
        Prepare(span.EndCapScene);
        if (span.FitMode == SpanFitMode.TrimFill) Prepare(span.FillScene);
        if (cache.Count == 0) return;

        foreach (var placement in span.Layout(radius).Placements())
        {
            if (span.Asset(placement.Piece) is not { } asset) continue;
            var template = cache[asset];
            if (visible)
                foreach (var mesh in template.Meshes)
                {
                    var pose = placement.Pose * mesh.Pose;
                    int source = sources.AddEvaluated(span, placement.Frame, pose, mesh.Bounds);
                    foreach (var surface in mesh.Surfaces)
                        surfaces.Add(surface with { Frame = placement.Frame, Pose = pose, OwnerIndex = source });
                }
            foreach (var local in template.Proxies)
            {
                var proxy = placement.Place(local);
                proxies.Add(proxy);
                sources.Proxies?.Add((span, proxy));
            }
        }
    }

    private static SpanTemplate ReadSpanTemplate(PackedScene asset, BakeVisibility visibility, Func<Material?, SurfaceTag> resolve)
    {
        var template = new SpanTemplate([], []);
        var root = asset.Instantiate();
        try { Visit(root, Transform3D.Identity, true); }
        finally { root.Free(); }
        return template;

        void Visit(Node node, Transform3D parent, bool inherited)
        {
            if (node is S3Space3D or Viewport) return;
            var pose = node is Node3D spatial ? parent * spatial.Transform : parent;
            bool visible = node is not Node3D { Visible: false } && (inherited || visibility == BakeVisibility.SelfVisible);
            if (GlomeMeshSource.Read(node) is { } mesh)
            {
                if (visible)
                {
                    List<MeshSurface> parts = [];
                    ExtractSurfaces(mesh, pose, S3Frame.Identity, resolve, parts, -1);
                    template.Meshes.Add(new(pose, mesh.Mesh.GetAabb(), parts.ToArray()));
                }
                if (mesh.CsgProxy(S3Frame.Identity, pose) is { } solid) template.Proxies.Add(solid);
            }
            if (node is CollisionShape3D { Disabled: false } shape &&
                CollisionShapeAdapter.Capture(S3Frame.Identity, pose, shape.Shape) is { } proxy) template.Proxies.Add(proxy);
            foreach (var child in node.GetChildren()) Visit(child, pose, visible);
        }
    }

    private static SpanWireEdge[] WireEdges(SpanTemplate template)
    {
        var edges = new HashSet<SpanWireEdge>();
        foreach (var mesh in template.Meshes)
            foreach (var surface in mesh.Surfaces)
            {
                var indices = surface.Indices;
                int n = indices.Length == 0 ? surface.Vertices.Length : indices.Length;
                Vector3 Vertex(int i) => mesh.Pose * surface.Vertices[indices.Length == 0 ? i : indices[i]];
                for (int i = 0; i + 2 < n; i += surface.Strip ? 1 : 3)
                {
                    var a = Vertex(i);
                    var b = Vertex(i + 1);
                    var c = Vertex(i + 2);
                    Add(a, b); Add(b, c); Add(c, a);
                }
            }
        var result = new SpanWireEdge[edges.Count];
        edges.CopyTo(result);
        return result;

        void Add(Vector3 a, Vector3 b)
        {
            if (a != b) edges.Add(a < b ? new(a,b) : new(b,a));
        }
    }
}
