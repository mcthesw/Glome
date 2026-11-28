#nullable enable
using System;
using System.Linq;
using Glome;
using Glome.Core;
using Godot;

namespace Glome.StandaloneTests;

internal static class CsgBakeChecks
{
    internal static void Run()
    {
        var space = new S3Space3D { Radius = 16, AutoStart = false, EquatorialGroundEnabled = false };
        try
        {
            var anchor = new S3Anchor3D { ChartPlacement = S3Anchor3D.PlacementMode.PositiveX };
            space.AddChild(anchor);
            var stone = new StandardMaterial3D();
            var lining = new StandardMaterial3D();
            var wall = new CsgBox3D { Size = new(6, 4, 1), Material = stone, UseCollision = true };
            var bore = new CsgCylinder3D { Radius = 1, Height = 3, Sides = 24, Rotation = new(Mathf.Pi/2, 0, 0),
                Material = lining, Operation = CsgShape3D.OperationEnum.Subtraction };
            anchor.AddChild(wall);
            wall.AddChild(bore);
            var options = new GlomeBakeOptions(CaptureAuthoringSources: true, IncludeOwners: true,
                ResolveMaterial: m => new(m == stone ? 1 : m == lining ? 2 : 0, 0));
            var bake = GlomeBaker.Bake(space, options);
            var chart = anchor.Frame(space.Radius);
            ArcRay Probe(float x)
            {
                var p = (chart.P + chart.Local(x,.117,4)/space.Radius).Unit();
                return new(p, (chart.P + chart.Local(x,.117,0)/space.Radius).Unit().Reject(p).Unit());
            }
            HitKind Hit(float x) => bake.Geometry.Trace(Probe(x), hi: .6).Kind;
            void Require(bool ok, string why)
            {
                if (!ok) throw new InvalidOperationException("CSG: " + why);
            }
            Require(Hit(.3f) == HitKind.Miss && Hit(1.6f) == HitKind.Triangle, "subtracted bore and surrounding wall");
            Require(bake.Geometry.Triangles.Select(t => t.Material).Distinct().Count() == 2 &&
                bake.TriangleOwners!.All(n => n == wall) && Enumerable.Range(0, bake.Geometry.Triangles.Length)
                .All(i => bake.AuthoringSources!.SourceForTriangle(i) == wall), "surface materials and pick mapping");
            Require(bake.Collision.ProxyCount == 1 && bake.Collision.SweepSphere(Probe(.3f), .6, .05, out _, out _)
                && bake.Collision.HasElevation, "documented walkable compound bounds");
            var physical = new StaticBody3D();
            wall.AddChild(physical);
            physical.AddChild(new CollisionShape3D { Position = new(2,0,0), Shape = new BoxShape3D { Size = new(2,4,1) } });
            bore.Radius = .2f;
            wall.MaterialOverride = stone;
            bake = GlomeBaker.Bake(space, options);
            Require(Hit(.3f) == HitKind.Triangle && bake.Geometry.Triangles.All(t => t.Material == 1),
                "same-frame boolean rebuild and material override");
            Require(bake.Collision.ProxyCount == 1 && !bake.Collision.SweepSphere(Probe(.3f), .6, .05, out _, out _)
                && bake.Collision.SweepSphere(Probe(1.6f), .6, .05, out _, out _), "authored proxy replaces compound bounds");
            bore.Operation = CsgShape3D.OperationEnum.Intersection;
            bake = GlomeBaker.Bake(space, options);
            Require(Hit(.03f) == HitKind.Triangle && Hit(1.6f) == HitKind.Miss, "intersection");
            bore.Operation = CsgShape3D.OperationEnum.Union;
            bake = GlomeBaker.Bake(space, options);
            Require(Hit(.3f) == HitKind.Triangle && Hit(1.6f) == HitKind.Triangle, "union");
            wall.Free();
            var cylinder = new CsgCylinder3D { Radius = 1.5f, Height = 3, UseCollision = true };
            space.AddChild(cylinder);
            bake = GlomeBaker.Bake(space, options);
            Require(bake.AuthoringSources!.Proxies.Single().Proxy.Shape == ProxyShape.Cylinder &&
                bake.Collision.SupportHeight(D4.Pole, 2) > 1.49, "walkable primitive cylinder");
            cylinder.UseCollision = false;
            Require(GlomeBaker.Bake(space).Collision.ProxyCount == 0, "use_collision disabled");
        }
        finally { space.Free(); }
        GD.Print("[GLOME-CSG-PASS] evaluated materials, three boolean operations, picking and collision policy");
    }
}
