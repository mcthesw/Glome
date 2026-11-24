using System;
using System.Linq;
using Glome.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Glome.Core.Tests;

[TestClass]
public sealed class GeometryTests
{
    private const double Radius = 13;

    private static GeodesicTriangle Face(double x, double z, int tag = 0)
    {
        ChartVertex Vertex(double dx, double y, D2 uv) => ChartVertex.Project(
            S3Frame.Identity, Radius, new(x + dx, y, z, 0), -D4.Back, uv);
        return new(Vertex(-0.5, -0.5, new(0, 0)), Vertex(0.5, -0.5, new(1, 0)),
                   Vertex(0, 0.5, new(0.5, 1)), tag);
    }

    [TestMethod]
    public void FullTurnTransportsTheEntireFrameBackToItsOrigin()
    {
        var frame = S3Frame.Identity;
        for (int i = 0; i < 1024; i++) frame = frame.Travel(frame.Back, Math.Tau / 1024);
        Assert.IsLessThan(1e-12, (frame.P - D4.Pole).Length);
        Assert.IsLessThan(1e-12, (frame.Right - D4.Right).Length);
        Assert.IsLessThan(1e-12, (frame.Up - D4.Up).Length);
        Assert.IsLessThan(1e-12, (frame.Back - D4.Back).Length);
    }

    [TestMethod]
    public void ExponentialAndLogarithmRoundTrip()
    {
        var tangent = new D4(0.35, -0.93, 0.6, 0);
        Assert.IsLessThan(1e-13, (S3Frame.LogMap(D4.Pole, S3Frame.ExpMap(D4.Pole, tangent)) - tangent).Length);
    }

    [TestMethod]
    public void TriangleReturnsProjectedUvAndCapContainsItsInterior()
    {
        var triangle = Face(0, 3);
        var ray = new ArcRay(D4.Pole, D4.Back);
        Assert.IsTrue(triangle.Intersect(ray, 0, Math.PI, out double t, out var uv));
        Assert.AreEqual(Math.Atan2(3, Radius), t, 1e-14);
        Assert.AreEqual(0.5, uv.X, 1e-13);
        Assert.AreEqual(0.5, uv.Y, 1e-13);
        foreach (var p in new[] {triangle.A.V, triangle.B.V, triangle.C.V, ray.At(t)})
            Assert.IsTrue(triangle.Cap.Center.Dot(p) >= triangle.Cap.CosAlpha);
        Assert.IsTrue(triangle.Cap.Intersects(ray, t - 1e-8, t + 1e-8));
        Assert.IsFalse(triangle.Cap.Intersects(ray, Math.PI, 1.5 * Math.PI));
    }

    [TestMethod]
    public void BvhMatchesBruteForceAndPermutesOwnersWithFaces()
    {
        var faces = Enumerable.Range(0, 60).Select(i => Face(i % 11 - 5, 2 + i % 7, i)).ToArray();
        var owners = faces.Select(f => f.Material).ToArray();
        var nodes = CapBvhBuilder.Build(faces, owners);
        CollectionAssert.AreEqual(faces.Select(f => f.Material).ToArray(), owners);
        var scene = new BakedGeometry(Radius, faces, nodes, false);
        foreach (var face in faces)
        {
            var ray = new ArcRay(D4.Pole, (face.A.V + face.B.V + face.C.V).Reject(D4.Pole).Unit());
            var hit = scene.Trace(ray);
            Assert.AreEqual(HitKind.Triangle, hit.Kind);
            Assert.AreEqual(scene.Trace(ray, bruteForce: true), hit);
        }
    }

    [TestMethod]
    public void TraceRespectsAngularIntervalTiesAndFilters()
    {
        var near = Face(0, 3);
        var faces = new[] {near, near, Face(0, 6, 1)};
        var scene = new BakedGeometry(Radius, faces, CapBvhBuilder.Build(faces));
        var ray = new ArcRay(D4.Pole, D4.Back);
        var first = scene.Trace(ray);
        Assert.AreEqual(0, first.Triangle);
        Assert.AreEqual(first, scene.Trace(ray, hi: double.PositiveInfinity));
        Assert.AreEqual(HitKind.Miss, scene.Trace(new(D4.Pole, D4.Right), hi: double.PositiveInfinity).Kind);
        Assert.IsFalse(near.Intersect(new(D4.Pole, D4.Right), 0, double.PositiveInfinity, out _, out _));
        Assert.AreEqual(1, scene.Trace(ray, ignoredTriangle: 0).Triangle);
        Assert.AreEqual(2, scene.Trace(ray, filter: f => f.Material == 1).Triangle);
        Assert.AreEqual(2, scene.Trace(ray, lo: first.T + 1e-8).Triangle);
        Assert.AreEqual(HitKind.Miss, scene.Trace(ray, hi: first.T - 1e-8).Kind);
    }

    [TestMethod]
    public void EquatorialGroundAndEyeHeightAreExplicit()
    {
        var pose = new WalkPose(S3Frame.Identity, 0);
        var eye = pose.Eye(Radius, 0.7);
        var ray = new ArcRay(eye.P, -eye.Up);
        var hit = new BakedGeometry(Radius, [], []).Trace(ray);
        Assert.AreEqual(HitKind.Ground, hit.Kind);
        Assert.AreEqual(0.7, hit.T * Radius, 1e-13);
        Assert.AreEqual(HitKind.Miss, new BakedGeometry(Radius, [], []).Trace(ray, includeEquator: false).Kind);
        Assert.AreEqual(HitKind.Miss, new BakedGeometry(Radius, [], [], false).Trace(ray).Kind);
    }
}
