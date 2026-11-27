#nullable enable
#if TOOLS
using System;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private Node3D[] _devices = [];

    private void Line(D4 a, D4 b, Color color, float width = 1)
    {
        if (Project(a, out var start) && Project(b, out var end) && start != end)
            overlay.DrawLine(start, end, color, width, true);
    }

    private D4 FrustumPoint(S3Frame p, int x, int y, float fov)
    {
        double height = 1.5 * Math.Tan(double.DegreesToRadians(Math.Clamp(fov, 1, 179)) / 2);
        return (p.P + p.Local(x * height * displayedSize.Aspect(), y * height, -1.5) / _space!.Radius).Unit();
    }

    private void DrawDevices()
    {
        if (!Shows(OverlayItem.LightsAndCameras) || displayedBake == null) return;
        foreach (var node in _devices)
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) continue;
            var active = drag?.Node == node ? drag : null;
            var p = active?.PreviewPivot ?? Pivot(node);
            var color = node is S3Camera3D ? Colors.Aquamarine : Colors.Gold;
            if (Project(p.P, out var center))
            {
                overlay.DrawCircle(center, 5, Colors.Black);
                overlay.DrawArc(center, 4, 0, Mathf.Tau, 16, color, 2, true);
            }
            // Full volumes belong to the selection so densely lit scenes remain pickable.
            if (GlomeNodeEditing.EditableTarget(node, plugin.GetTree().EditedSceneRoot) != selected) continue;
            if (node is OmniLight3D omni)
            {
                double arc = Math.Clamp((active?.Scalar ?? omni.OmniRange) / _space!.Radius, 0, Math.PI);
                foreach (var (u,v) in new[] { (p.Up,p.Back), (p.Right,p.Up), (p.Right,p.Back) })
                {
                    D4 At(int i) => new ArcRay(p.P,u * Math.Cos(Math.Tau*i/48) + v * Math.Sin(Math.Tau*i/48)).At(arc);
                    for (int i = 0; i < 48; i++) Line(At(i), At(i+1), color);
                }
            }
            if (node is DirectionalLight3D)
            {
                var tip = p.Travel(-p.Back, 2/_space!.Radius).P;
                Line(p.P, tip, color, 2);
                Line(tip, (p.P+p.Local(.18,0,-1.65)/_space.Radius).Unit(), color);
                Line(tip, (p.P+p.Local(-.18,0,-1.65)/_space.Radius).Unit(), color);
            }
            if (node is S3Camera3D camera)
            {
                float fov = active?.Scalar ?? camera.Fov;
                foreach (int x in new[] { -1,1 })
                    foreach (int y in new[] { -1,1 })
                    {
                        var corner = FrustumPoint(p,x,y,fov);
                        Line(p.P, corner, color);
                        if (x == -1) Line(corner, FrustumPoint(p,1,y,fov), color);
                        if (y == -1) Line(corner, FrustumPoint(p,x,1,fov), color);
                    }
            }
        }
    }

    private bool PickDevice(Vector2 mouse)
    {
        if (!Shows(OverlayItem.LightsAndCameras) || displayedBake == null) return false;
        Node3D? nearest = null;
        float best = 8;
        double arc = Math.PI;
        foreach (var node in _devices)
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) continue;
            var point = Pivot(node).P;
            if (!Project(point, out var screen)) continue;
            float pixels = mouse.DistanceTo(screen);
            double distance = displayed.P.AngleTo(point);
            if (pixels > best || pixels == best && distance >= arc) continue;
            (nearest, best, arc) = (node, pixels, distance);
        }
        if (nearest == null) return false;
        SelectPicked(nearest);
        _pivot = Pivot(nearest).P;
        UpdateDistance();
        return true;
    }

    private void DrawCollision()
    {
        if (!Shows(OverlayItem.Collision) || displayedBake?.AuthoringSources is not { } sources) return;
        foreach (var (node, proxy) in sources.Proxies)
        {
            if (!GodotObject.IsInstanceValid(node) || !GlomeWireframe.MovesWith(node, selected!)) continue;
            D4 Point(float x, float y, float z) => GlomeNodeEditing.Lift(proxy.Chart,
                proxy.Pose * (new Vector3(x,y,z) * proxy.Half), displayedBake.Geometry.Radius);
            var color = Colors.Cyan;
            if (proxy.Shape == ProxyShape.Cylinder)
            {
                for (int i = 0; i < 24; i++)
                {
                    var (a,b) = MathF.SinCos(Mathf.Tau*i/24);
                    var (c,d) = MathF.SinCos(Mathf.Tau*(i+1)/24);
                    Line(Point(a,-1,b), Point(c,-1,d), color);
                    Line(Point(a,1,b), Point(c,1,d), color);
                    if (i % 6 == 0) Line(Point(a,-1,b), Point(a,1,b), color);
                }
                continue;
            }
            var top = new Vector2[4];
            bool visible = true;
            for (int i = 0; i < 4; i++)
            {
                int x = i is 0 or 3 ? -1 : 1, z = i < 2 ? -1 : 1;
                int nextX = i is 2 or 3 ? -1 : 1, nextZ = i is 0 or 3 ? -1 : 1;
                var a = Point(x,1,z);
                visible &= Project(a, out top[i]);
                Line(Point(x,-1,z), Point(nextX,-1,nextZ), color);
                Line(Point(x,-1,z), a, color);
                Line(a, Point(nextX,1,nextZ), proxy.Walkable ? Colors.LimeGreen : color, proxy.Walkable ? 2 : 1);
            }
            if (proxy.Walkable && visible && Math.Abs((top[1]-top[0]).Cross(top[3]-top[0])) > 1)
                overlay.DrawColoredPolygon(top, new Color(Colors.LimeGreen, .28f));
        }
    }
}
#endif
