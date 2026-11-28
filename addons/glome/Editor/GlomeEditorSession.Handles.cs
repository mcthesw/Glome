#nullable enable
#if TOOLS
using System;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private static Color HandleColor(GizmoHandle h) => h switch
    {
        GizmoHandle.X or GizmoHandle.Pitch or GizmoHandle.YZ => Colors.Salmon,
        GizmoHandle.Y or GizmoHandle.Yaw or GizmoHandle.Ground => Colors.LightGreen,
        GizmoHandle.Z or GizmoHandle.Roll or GizmoHandle.XY => Colors.DeepSkyBlue,
        _ => Colors.Gold
    };

    private void Handles(S3Frame p, Action<GizmoHandle, Vector2, Vector2> segment)
    {
        double r = _space!.Radius;
        void Edge(GizmoHandle h, D4 a, D4 b)
        {
            if (Project(a, out var start) && Project(b, out var end)) segment(h, start, end);
        }
        void Cap(GizmoHandle h, D4 tip)
        {
            if (!Project(tip, out var center)) return;
            const int half = 4;
            segment(h, center + new Vector2(-half, 0), center + new Vector2(half, 0));
            segment(h, center + new Vector2(0, -half), center + new Vector2(0, half));
        }
        if (_tool == GizmoTool.Select || _tool == GizmoTool.Scale && selected is Light3D or S3Camera3D or S3GeodesicSpan3D) return;
        if (_tool == GizmoTool.Rotate)
        {
            foreach (var ring in new[] { GizmoHandle.Pitch, GizmoHandle.Yaw, GizmoHandle.Roll })
            {
                var plane = GizmoPlane.For(p, ring, displayed.P);
                D4 At(double t) => p.Travel(plane.U * Math.Cos(t) + plane.V * Math.Sin(t), 1 / r).P;
                for (int i = 0; i < 48; i++) Edge(ring, At(Math.Tau*i/48), At(Math.Tau*(i+1)/48));
            }
        }
        else
        {
            foreach (var (h, axis) in new[] { (GizmoHandle.X,p.Right), (GizmoHandle.Y,p.Up), (GizmoHandle.Z,p.Back) })
            {
                var tip = p.Travel(axis, 1.6/r).P;
                Edge(h, p.P, tip);
                Cap(h, tip);
            }
            foreach (var h in new[] { GizmoHandle.Ground, GizmoHandle.XY, GizmoHandle.YZ })
            {
                var plane = GizmoPlane.For(p, h, displayed.P);
                D4 At(double a, double b) => (p.P + (plane.U*a + plane.V*b)/r).Unit();
                var outer = At(.48,.48);
                Edge(h, At(.48,.12), outer);
                Edge(h, outer, At(.12,.48));
            }
        }
        if (!Shows(OverlayItem.LightsAndCameras)) return;
        if (selected is OmniLight3D omni)
            Cap(GizmoHandle.Range, p.Travel(p.Right, Math.Clamp((drag?.Scalar ?? omni.OmniRange)/r, 0, Math.PI)).P);
        if (selected is DirectionalLight3D) Cap(GizmoHandle.Direction, p.Travel(-p.Back, 2/r).P);
        if (selected is S3Camera3D camera)
            Cap(GizmoHandle.Fov, FrustumPoint(p, 0, 1, drag?.Scalar ?? camera.Fov));
    }

    private GizmoHandle? HitHandle(S3Frame pivot, Vector2 mouse)
    {
        GizmoHandle? hit = null;
        float best = 7;
        Handles(pivot, (handle, a, b) =>
        {
            var d = b - a;
            if (d.LengthSquared() < .00001f) return;
            float distance = mouse.DistanceTo(a + d * Mathf.Clamp((mouse-a).Dot(d)/d.LengthSquared(), 0, 1));
            if (distance <= best) { best = distance; hit = handle; }
        });
        if (_tool == GizmoTool.Move && Project(pivot.P, out var center) && center.DistanceTo(mouse) < 5) return GizmoHandle.Ground;
        return hit;
    }
}
#endif
