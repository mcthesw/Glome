using System;
using Glome.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glome.Core.Tests;

[TestClass]
public sealed class AcousticTests
{
    [TestMethod]
    public void DirectAndComplementaryPathsMeetAroundTheGreatCircle()
    {
        var listener = S3Frame.OnEquator(1.3, 0.2).Roll(0.7);
        var ray = new ArcRay(listener.P, listener.Right);
        var path = S3AcousticPath.Resolve(listener, ray.At(0.75), 10);
        Assert.IsLessThan(1e-13, (path.LocalDirection - D4.Right).Length);
        Assert.AreEqual(7.5, path.DirectDistance, 1e-13);
        Assert.AreEqual(10 * Math.Tau, path.DirectDistance + path.ComplementaryDistance, 1e-13);
        Assert.IsLessThan(1e-13, (new ArcRay(listener.P, -listener.Right).At(path.ComplementaryDistance / 10) - ray.At(0.75)).Length);
    }

    [TestMethod]
    public void CoincidentAndAntipodalEmittersHaveDeterministicLocalDirections()
    {
        var listener = S3Frame.OnEquator(0.8, 2.1).Yaw(1.4);
        var same = S3AcousticPath.Resolve(listener, listener.P, 13);
        Assert.AreEqual(D4.Up, same.LocalDirection);
        Assert.AreEqual(0, same.DirectDistance);
        Assert.AreEqual(Math.Tau * 13, same.ComplementaryDistance);
        var opposite = S3AcousticPath.Resolve(listener, -listener.P, 13, -D4.Back);
        Assert.AreEqual(-D4.Back, opposite.LocalDirection);
        Assert.AreEqual(Math.PI * 13, opposite.DirectDistance);
        Assert.AreEqual(opposite.DirectDistance, opposite.ComplementaryDistance);
    }
}
