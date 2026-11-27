#nullable enable
#if TOOLS
using System;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed class GlomeGizmoDrag
{
    internal readonly Node3D Node;
    internal readonly GizmoHandle Handle;
    internal readonly GizmoPlane Plane;
    internal readonly S3Frame Pivot, Chart;
    internal readonly Transform3D Initial, Parent;
    internal readonly GlomeWireframe Wire;
    internal readonly float InitialScalar;
    internal Vector2 Last, Metres, Pixels;
    internal double Radians;
    internal Transform3D Local, ChartPose;
    internal S3Frame PreviewPivot;
    internal S3Frame? Spin;
    internal float Scalar;

    internal GlomeGizmoDrag(Node3D node, GizmoHandle handle, S3Frame eye, Vector2 mouse, S3BakeSnapshot snapshot)
    {
        Node = node;
        Handle = handle;
        Last = mouse;
        double radius = snapshot.Geometry.Radius;
        Pivot = PreviewPivot = GlomeAuthoring.Pivot(node, radius);
        Plane = GizmoPlane.For(Pivot, handle, eye.P);
        (Chart, ChartPose) = GlomeNodeEditing.Pose(node, radius);
        Initial = Local = node.Transform;
        Parent = GlomeNodeEditing.Parent(node, radius).Pose;
        Wire = GlomeWireframe.Capture(node, snapshot);
        InitialScalar = Scalar = node switch { OmniLight3D omni => omni.OmniRange, S3Camera3D camera => camera.Fov, _ => 0 };
    }

    internal bool IsPlane => Handle is GizmoHandle.Ground or GizmoHandle.XY or GizmoHandle.YZ;

    internal void Preview(double radius, bool scale, bool snap, out double arc, out double angle)
    {
        var movement = IsPlane ? Metres : new Vector2(Metres.X, 0);
        arc = angle = 0;
        if (Handle == GizmoHandle.Range)
        {
            Scalar = (float)Math.Clamp(InitialScalar + GlomeAuthoring.ArcStep(Metres.X, snap), .01, Math.PI * radius);
            return;
        }
        if (Handle == GizmoHandle.Fov)
        {
            double height = Math.Tan(double.DegreesToRadians(InitialScalar) / 2) + Metres.X / 1.5;
            Scalar = (float)Math.Clamp(double.RadiansToDegrees(GlomeAuthoring.AngleStep(2 * Math.Atan(height), snap)), 1, 179);
            return;
        }

        var local = Initial;
        var next = Pivot;
        if (Handle == GizmoHandle.Direction)
        {
            var turn = Basis.FromEuler(new((float)GlomeAuthoring.AngleStep(-Pixels.Y * .01, snap),
                (float)GlomeAuthoring.AngleStep(-Pixels.X * .01, snap), 0));
            var axes = Initial.Basis.Orthonormalized();
            local.Basis = axes * turn * axes.Inverse() * Initial.Basis;
        }
        else if (scale)
        {
            var factors = Vector3.One;
            static float Factor(float offset, bool snap)
            {
                float value = (float)Math.Exp(Math.Clamp(offset / 1.6, -6, 6));
                return snap ? Math.Max(.1f, Mathf.Snapped(value, .1f)) : value;
            }
            int first = Handle switch { GizmoHandle.Y or GizmoHandle.YZ => 1, GizmoHandle.Z => 2, _ => 0 };
            factors[first] = Factor(movement.X, snap);
            if (IsPlane) factors[Handle == GizmoHandle.XY ? 1 : 2] = Factor(movement.Y, snap);
            local.Basis = Initial.Basis * Basis.FromScale(factors);
        }
        else if (GizmoPlane.IsRing(Handle))
        {
            angle = GlomeAuthoring.AngleStep(Radians, snap);
            next = GizmoPlane.Turn(Pivot, Handle, angle);
            if (Node is not S3Anchor3D)
            {
                var axis = Handle switch { GizmoHandle.Pitch => Initial.Basis.X, GizmoHandle.Roll => Initial.Basis.Z, _ => -Initial.Basis.Y };
                local.Basis = new Basis(axis.Normalized(), (float)angle) * Initial.Basis;
            }
        }
        else
        {
            arc = GlomeAuthoring.ArcStep(movement.Length(), snap);
            if (arc > 1e-9) next = Pivot.Travel((Plane.U * movement.X + Plane.V * movement.Y).Unit(), arc / radius);
            if (Node is not S3Anchor3D && movement != Vector2.Zero)
            {
                if (!GlomeNodeEditing.TryLocalPoint(Chart, Parent, radius, next.P, out var origin)) return;
                local.Origin = origin;
            }
        }
        if (Node is S3Anchor3D && !scale)
        {
            PreviewPivot = next;
            Spin = next == Pivot ? null : GlomeAuthoring.Between(Pivot, next);
            return;
        }
        if (!GlomeNodeEditing.Invertible(local.Basis)) return;
        Local = local;
        if (Node is S3Anchor3D anchor)
        {
            var pose = anchor.ChartPose(radius).Pose;
            // Right-multiplying by the local change preserves an absolute anchor's LocalOffset.
            ChartPose = pose * (Initial.AffineInverse() * local);
        }
        else ChartPose = Parent * local;
        PreviewPivot = ChartCamera.FromTangent(Chart, ChartPose, radius);
    }
}
#endif
