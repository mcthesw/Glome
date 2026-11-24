#nullable enable
using System;

namespace Glome.Core;

public readonly record struct WalkPose(S3Frame Ground, double Pitch)
{
    public S3Frame Eye(double radius, double eyeHeight) => Ground.Travel(D4.Up, eyeHeight / radius).Pitch(Pitch);

    public static WalkPose LookingAt(S3Frame ground, D4 target, double radius, double eyeHeight)
    {
        D4 back = -target.Reject(ground.P).Reject(D4.Up).Unit();
        var level = new S3Frame(-D4.Hodge(D4.Up, back, ground.P), D4.Up, back, ground.P);
        var eye = level.Travel(D4.Up, eyeHeight / radius);
        return new(level, Math.Asin(Math.Clamp(target.Reject(eye.P).Unit().Dot(eye.Up), -1, 1)));
    }
}
