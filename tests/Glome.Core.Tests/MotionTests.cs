using System;
using System.Collections.Generic;
using Glome.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glome.Core.Tests;

[TestClass]
public sealed class MotionTests
{
    private const double Radius = 12;
    private static readonly S3Frame Origin = S3Frame.Identity;

    private static CollisionProxy Obstacle(double z, D4 half, ProxyShape shape)
    {
        var transform = new ProxyTransform(D4.Right, D4.Up, new(0, 0, 1, -z), new(1, 1, 1, 0), Math.Abs(z));
        return new(Origin, Radius, transform, half, shape);
    }

    private static double ChartZ(S3Frame frame) => Radius * frame.P.Z / frame.P.W;

    [TestMethod]
    public void FloorlessPlacementCanCrossTheEquatorAndRetainsAllFourFrameAxes()
    {
        var space = new S3CollisionWorld(Radius, [], equatorialGroundEnabled: false);
        var arrival = Origin.Travel(Origin.Up, 0.2).Roll(0.12);
        var motor = new S3MotorCore(Radius, new(arrival, 0), new(0.2,1.8,1.1), space);
        Assert.AreEqual(arrival, motor.Camera);
        motor.ToggleFlight();
        Assert.IsTrue(motor.Flying);
        motor.Move(0,-1,0,6);
        var expected = arrival.Travel(-arrival.Up,6/Radius);
        Assert.IsLessThan(0, motor.Camera.P.Y);
        Assert.IsLessThan(1e-12, (expected.P-motor.Camera.P).Length);
        Assert.IsLessThan(1e-12, (expected.Right-motor.Camera.Right).Length);
        motor.ClearPlacement();
        Assert.IsLessThan(1e-12, (expected.Up-motor.Camera.Up).Length);
    }

    [TestMethod]
    public void BoxStopsLongSweepsAndSlidesAlongItsWall()
    {
        var wall = Obstacle(4, new(8, 2, 0.1, 0), ProxyShape.Box);
        var world = new S3CollisionWorld(Radius, [wall]);
        var positions = new List<D4>();
        var straight = world.Move(Origin, D4.Back, 20, 0.25, 1.1, positions.Add);
        Assert.AreEqual(3.65, ChartZ(straight), 0.002);
        Assert.AreEqual(straight.P, positions[^1]);
        var slide = world.Move(Origin, (D4.Back + D4.Right * 0.4).Unit(), 7, 0.25, 1.1);
        Assert.IsLessThan(3.66, ChartZ(slide));
        Assert.IsGreaterThan(2, slide.P.X * Radius / slide.P.W);
    }

    [TestMethod]
    public void CylinderDepenetratesAndStableIdsReplaceOnlyTheSelectedShape()
    {
        var cylinder = Obstacle(0, new(1, 2, 1, 0), ProxyShape.Cylinder);
        var wall = Obstacle(5, new(6, 2, 0.25, 0), ProxyShape.Box);
        var world = new S3CollisionWorld(Radius, [cylinder, wall]);
        var clear = world.ClearPlacement(Origin, 0.3, 1.2);
        Assert.AreEqual(1.3, Math.Abs(clear.P.X / clear.P.W) * Radius, 0.002);
        Assert.AreEqual(0, clear.P.Y, 1e-12);
        var shifted = Obstacle(3, new(1, 2, 1, 0), ProxyShape.Cylinder);
        world.ReplaceProxy(0, shifted);
        Assert.AreEqual(Origin, world.ClearPlacement(Origin, 0.3, 1.2));
        Assert.AreEqual(1.7, ChartZ(world.Move(Origin, D4.Back, 8, 0.3, 1.2)), 0.002);
        world.ReplaceProxy(0, Obstacle(-3, new(1, 2, 1, 0), ProxyShape.Cylinder));
        Assert.AreEqual(4.45, ChartZ(world.Move(Origin, D4.Back, 8, 0.3, 1.2)), 0.002);
        Assert.AreEqual(2, world.ProxyCount);
    }

    [TestMethod]
    public void BodyHeightAndPaddingAffectClearanceIndependently()
    {
        // Inverse Y translation puts the box two metres above the equator.
        var transform = new ProxyTransform(D4.Right, new(0, 1, 0, -2), new(0, 0, 1, -3),
                                           new(1, 1, 1, 0), Math.Sqrt(13));
        var world = new S3CollisionWorld(Radius, [new(Origin, Radius, transform, new(4, 0.25, 0.5, 0), ProxyShape.Box)]);
        var shortBody = world.Move(Origin, D4.Back, 5, 0.2, 1.4);
        var tallBody = world.Move(Origin, D4.Back, 5, 0.2, 1.7);
        Assert.AreEqual(5, Radius * Origin.P.AngleTo(shortBody.P), 1e-12);
        Assert.AreEqual(2.3, ChartZ(tallBody), 0.002);
    }

    [TestMethod]
    public void WalkAndFlightTransportTheViewAndItsEquatorialFootprint()
    {
        var motor = new S3MotorCore(Radius, new(Origin, 0), new(0.2, 1.8, 1.1));
        motor.Look(0.35, 0.2);
        motor.Move(0, 1, 1, 4);
        var foot = Origin.Yaw(0.35);
        foot = foot.Travel(foot.Back, 4 / Radius);
        var expected = new WalkPose(foot, 0.2).Eye(Radius, 1.1);
        Assert.IsLessThan(1e-12, (expected.P - motor.Camera.P).Length);
        motor.ToggleFlight();
        Assert.AreEqual(expected.P.X, motor.Camera.P.X, 1e-12);
        motor.Move(0, 0, 1, 8, 0.2);
        var flight = expected.Roll(0.2).Travel(expected.Back, 8 / Radius);
        Assert.IsLessThan(1e-12, (flight.P - motor.Camera.P).Length);
        Assert.IsLessThan(1e-12, (flight.Right - motor.Camera.Right).Length);
        motor.ToggleFlight();
        Assert.IsFalse(motor.Flying);
        Assert.IsLessThan(1e-12, (flight.P.Reject(D4.Up).Unit() - motor.Camera.P.Reject(D4.Up).Unit()).Length);
        Assert.AreEqual(Math.Sin(1.1 / Radius), motor.Camera.P.Y, 1e-12);
    }
}
