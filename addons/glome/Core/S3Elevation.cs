using System;

namespace Glome.Core;

// The walking frame stays on the equator for heading transport. The collider and
// eye are lifted independently along its vertical great circle.
internal sealed class S3Elevation
{
    internal const double StepUp = 0.45;
    internal double Height { get; private set; }
    private double _fallSpeed;
    private CollisionProxy? _standingOn;

    internal void Place(S3CollisionWorld world, D4 ground, double ceiling)
    {
        Height = world.SupportHeight(ground, ceiling + StepUp, out _standingOn);
        _fallSpeed = 0;
    }

    internal S3Frame Clear(S3CollisionWorld world, S3Frame ground, BodyProfile body)
    {
        var foot = ground.Travel(D4.Up, Height / world.Radius);
        var clear = world.ClearPlacement(foot, body.Radius, body.Height);
        return FollowFootprint(ground, clear.P);
    }

    internal S3Frame Move(S3CollisionWorld world, S3Frame ground, BodyProfile body,
        double x, double z, double metres, double seconds)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(metres / 0.18, seconds * 60)));
        double dt = seconds / steps;
        for (int i = 0; i < steps; i++)
        {
            if (metres > 0)
            {
                var foot = ground.Travel(D4.Up, Height / world.Radius);
                var moved = world.Move(foot, ground.Local(x, 0, z).Unit(), metres / steps, body.Radius, body.Height);
                ground = FollowFootprint(ground, moved.P);
            }
            double support = world.SupportHeight(ground.P, Height + StepUp, out var surface);
            // Follow descending treads, but keep vertical velocity when leaving a ledge.
            if (support >= Height || (surface is not null && surface == _standingOn && _fallSpeed == 0))
                Height = support;
            else
            {
                const double gravity = 9.81;
                Height = Math.Max(support, Height - (_fallSpeed + 0.5 * gravity * dt) * dt);
                _fallSpeed += gravity * dt;
            }
            _standingOn = Height == support ? surface : null;
            if (Height == support) _fallSpeed = 0;
        }
        return ground;
    }

    private static S3Frame FollowFootprint(S3Frame ground, D4 lifted)
    {
        D4 q = lifted.Reject(D4.Up).Unit();
        D4 tangent = q.Reject(ground.P);
        if (tangent.Length < 1e-14) return ground;
        return ground.Travel(tangent.Unit(), Math.Atan2(tangent.Length, q.Dot(ground.P))).Orthonormalized();
    }
}
