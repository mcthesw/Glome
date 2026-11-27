#nullable enable
using System;

namespace Glome.Core;

public readonly record struct WalkPose(S3Frame Ground, double Pitch, double Elevation = 0)
{
    public S3Frame Eye(double radius, double eyeHeight) => Ground.Travel(D4.Up, (Elevation + eyeHeight) / radius).Pitch(Pitch);

    public static WalkPose LookingAt(S3Frame ground, D4 target, double radius, double eyeHeight, double elevation = 0)
    {
        D4 horizontal = target.Reject(ground.P).Reject(D4.Up);
        bool vertical = horizontal.Length < 1e-12;
        D4 back = vertical ? ground.Back.Reject(ground.P).Reject(D4.Up) : -horizontal;
        if (back.Length < 1e-12)
        {
            D4 axis = Math.Abs(ground.P.X) < 0.8 ? D4.Right : D4.Back;
            back = axis.Reject(ground.P);
        }
        back = back.Unit();
        var level = new S3Frame(-D4.Hodge(D4.Up, back, ground.P), D4.Up, back, ground.P);
        var eye = level.Travel(D4.Up, (elevation + eyeHeight) / radius);
        D4 sight = target.Reject(eye.P);
        double pitch = sight.Length < 1e-12 ? 0 : vertical
            ? Math.CopySign(Math.PI/2, sight.Dot(eye.Up))
            : Math.Asin(Math.Clamp(sight.Unit().Dot(eye.Up),-1,1));
        return new(level, pitch, elevation);
    }
}
