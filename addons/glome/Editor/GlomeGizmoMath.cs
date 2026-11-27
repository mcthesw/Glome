#nullable enable
#if TOOLS
using Glome.Core;

namespace Glome.Editor;

internal enum GizmoHandle { Ground, X, Y, Z, Pitch, Yaw, Roll }

internal readonly record struct GizmoPlane(D4 U, D4 V, D4 Normal)
{
    internal static bool IsRing(GizmoHandle handle) => handle is GizmoHandle.Pitch or GizmoHandle.Yaw or GizmoHandle.Roll;

    internal static GizmoPlane For(S3Frame p, GizmoHandle handle, D4 eye)
    {
        if (handle == GizmoHandle.Pitch) return new(p.Up, p.Back, p.Right);
        if (handle == GizmoHandle.Roll) return new(p.Right, p.Up, p.Back);
        if (handle is GizmoHandle.Yaw or GizmoHandle.Ground) return new(p.Right, p.Back, p.Up);

        // The plane containing an axis and facing the viewer gives the largest
        // projected handle, even when the author works in a vertical chart.
        var axis = handle switch { GizmoHandle.X => p.Right, GizmoHandle.Y => p.Up, _ => p.Back };
        var normal = eye.Reject(p.P).Reject(axis);
        if (normal.Length < 1e-9) normal = handle == GizmoHandle.Y ? p.Back : p.Up;
        normal = normal.Unit();
        return new(axis, D4.Hodge(axis, normal, p.P).Unit(), normal);
    }

    internal static S3Frame Turn(S3Frame p, GizmoHandle handle, double radians) => handle switch
    {
        GizmoHandle.Pitch => p.Pitch(radians),
        GizmoHandle.Roll => p.Roll(radians),
        _ => p.Yaw(radians)
    };
}
#endif
