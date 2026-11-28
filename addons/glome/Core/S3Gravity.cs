#nullable enable
namespace Glome.Core;

// Uniform ambient potential restricted to S³. The returned acceleration is
// tangent at the sample position, in m/s².
public readonly record struct S3Gravity(D4 Vertical, double Strength)
{
    public D4 SampleAcceleration(D4 position) => -Vertical.Reject(position) * Strength;
    public static S3Gravity Standard => new(D4.Up, S3MotorCore.Gravity);
}
