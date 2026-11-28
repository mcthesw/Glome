using System;
using Glome.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glome.Core.Tests;

[TestClass]
public sealed class CapsuleTests
{
    private const double Radius = 20;
    private static readonly S3Frame Chart = S3Frame.Identity.Roll(0.63).Pitch(-0.42);
    private static readonly S3Capsule Body = new(0.27, 1.38);

    private static CollisionProxy Box(double x, double y, D4 half, bool deck = false)
    {
        var transform = new ProxyTransform(new(1, 0, 0, -x), new(0, 1, 0, -y), D4.Back,
            new(1, 1, 1, 0), Math.Sqrt(x * x + y * y));
        return new(Chart, Radius, transform, half, ProxyShape.Box, deck);
    }

    [TestMethod]
    public void RotatingUnderCeilingHitsWithoutTranslatingTheEye()
    {
        var ceiling = Box(0, 1, new(2, 0.05, 2, 0));
        var world = new S3CollisionWorld(Radius, [ceiling], false).CreateCapsuleWorld();
        var flat = Chart.Roll(Math.PI / 2);
        Assert.IsTrue(world.SweepCapsule(Body, flat, flat.Roll(Math.PI / 2), out var hit));
        Assert.IsTrue(hit.Fraction is > 0.1 and < 0.8);
    }

    [TestMethod]
    public void WalkableStepRequiresHeadroom()
    {
        var tread = Box(0.7, 0.08, new(0.45, 0.08, 0.8, 0), deck: true);
        var lintel = Box(0, 2.04, new(1, 0.05, 0.8, 0));
        CharacterGround Solver(params CollisionProxy[] obstacles) =>
            new(new S3CollisionWorld(Radius, obstacles, false).CreateCapsuleWorld(), Body, 0.18, 0.32);
        var eye = Body.Eye(Chart, Radius);
        var delta = eye.Right * 0.5;
        Assert.IsTrue(Solver(tread).StepUp(eye, delta, out var stepped, out var hit));
        Assert.IsTrue(hit.Walkable && (stepped.P - eye.P).Dot(Chart.Up) * Radius > 0.14);
        Assert.IsFalse(Solver(tread, lintel).StepUp(eye, delta, out var unchanged, out _));
        Assert.AreEqual(eye, unchanged);
    }

    [TestMethod]
    public void GravityIsTangentialAndVanishesAtItsPoles()
    {
        var gravity = new S3Gravity(Chart.Up, 9);
        Assert.AreEqual(0, gravity.SampleAcceleration(Chart.P).Dot(Chart.P), 1e-12);
        Assert.AreEqual(9, gravity.SampleAcceleration(Chart.P).Length, 1e-12);
        Assert.AreEqual(0, gravity.SampleAcceleration(Chart.Up).Length, 1e-12);
    }
}
