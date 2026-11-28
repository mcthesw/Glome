#nullable enable
using Glome.Core;
using Godot;

namespace Glome;

// The evaluated CSG mesh is in its root's local coordinates, just like a MeshInstance3D.
internal readonly record struct GlomeMeshSource(GeometryInstance3D Node, Mesh Mesh)
{
    public static GlomeMeshSource? Read(Node node)
    {
        if (node is MeshInstance3D { Mesh: { } mesh } instance) return new(instance, mesh);
        if (node is not CsgShape3D csg || !csg.IsRootShape()) return null;
        // Godot 4.7 defers this method. A synchronous bake (including space._Ready)
        // must see edits made in the same frame, before the deferred queue drains.
        csg.Call("_update_shape");
        return csg.BakeStaticMesh() is { } evaluated && evaluated.GetSurfaceCount() > 0
            ? new(csg, evaluated) : null;
    }

    public Material? MaterialAt(int surface) => Node is MeshInstance3D instance
        ? instance.GetActiveMaterial(surface) : Node.MaterialOverride ?? Mesh.SurfaceGetMaterial(surface);

    public CollisionShapeAdapter.Input? CsgProxy(S3Frame frame, Transform3D pose)
    {
        if (Node is not CsgShape3D { UseCollision: true } root || HasAuthoredProxy(root) ||
            Mathf.IsZeroApprox(pose.Basis.Determinant())) return null;
        bool compound = false;
        foreach (var child in root.GetChildren())
            if (child is CsgShape3D { Visible: true }) { compound = true; break; }
        if (!compound && root is CsgCylinder3D { Cone: false } cylinder)
            return new(frame, pose, new(cylinder.Radius, cylinder.Height / 2, cylinder.Radius), ProxyShape.Cylinder, true);
        // The motor supports convex box/cylinder proxies. An evaluated local AABB
        // also handles combinations, empty intersections and non-box primitives.
        var bounds = Mesh.GetAabb();
        return bounds.HasVolume()
            ? new(frame, pose.TranslatedLocal(bounds.GetCenter()), bounds.Size / 2, ProxyShape.Box, true)
            : null;
    }

    private static bool HasAuthoredProxy(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is S3Anchor3D or S3Space3D or Viewport) continue;
            if (child is CollisionShape3D { Disabled: false, Shape: BoxShape3D or CylinderShape3D } ||
                HasAuthoredProxy(child)) return true;
        }
        return false;
    }
}
