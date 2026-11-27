#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Glome.Core;

public readonly record struct CapNode(SphericalCap Cap, int First, int Count, int Escape);

public static class CapBvhBuilder
{
    private const int BinCount = 12;
    private readonly record struct Hull(Vector4 Min, Vector4 Max)
    {
        public static Hull Empty => new(new(float.PositiveInfinity), new(float.NegativeInfinity));
        public Hull Add(Vector4 p) => new(Vector4.Min(Min, p), Vector4.Max(Max, p));
        public Hull Union(Hull b) => new(Vector4.Min(Min, b.Min), Vector4.Max(Max, b.Max));

        // A surface-area proxy for a 3D patch embedded obliquely in four coordinates.
        public float Area
        {
            get
            {
                var d = Max - Min;
                return d.X * (d.Y + d.Z + d.W) + d.Y * (d.Z + d.W) + d.Z * d.W;
            }
        }
    }

    private readonly record struct Item(GeodesicTriangle Triangle, Vector4 Center, Hull Bounds);
    private struct Bin
    {
        public Hull Bounds;
        public int Count;
        public void Add(Hull bounds, int count)
        {
            if (count == 0) return;
            Bounds = Count == 0 ? bounds : Bounds.Union(bounds);
            Count += count;
        }
        public readonly float Cost => Count == 0 ? 0 : Bounds.Area * Count;
    }

    private interface IPermutation { void Swap(int left, int right); }
    private readonly struct GeometryOnly : IPermutation { public void Swap(int left, int right) { } }
    private readonly struct Permutation<T>(IList<T> entries) : IPermutation
    {
        public void Swap(int left, int right) => (entries[left], entries[right]) = (entries[right], entries[left]);
    }

    public static CapNode[] Build(GeodesicTriangle[] triangles) => Build(triangles, new GeometryOnly());
    public static CapNode[] Build<T>(GeodesicTriangle[] triangles, IList<T> owners)
    {
        if (owners.Count != triangles.Length) throw new ArgumentException("Expected one owner per triangle.", nameof(owners));
        return Build(triangles, new Permutation<T>(owners));
    }
    private static CapNode[] Build<TPermutation>(GeodesicTriangle[] triangles, TPermutation owners)
        where TPermutation : struct, IPermutation
    {
        static Vector4 Point(D4 p) => new((float)p.X, (float)p.Y, (float)p.Z, (float)p.W);
        var items = new Item[triangles.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var t = triangles[i];
            items[i] = new(t, Point(t.Cap.Center), Hull.Empty.Add(Point(t.A.V)).Add(Point(t.B.V)).Add(Point(t.C.V)));
        }
        var nodes = new List<CapNode>();
        if (items.Length > 0) Partition(0, items.Length);
        for (int i = 0; i < items.Length; i++) triangles[i] = items[i].Triangle;
        return nodes.ToArray();

        void Partition(int first, int count)
        {
            int slot = nodes.Count;
            nodes.Add(default);
            Hull bounds = Hull.Empty, centers = Hull.Empty;
            for (int i = first; i < first + count; i++)
            {
                bounds = bounds.Union(items[i].Bounds);
                centers = centers.Add(items[i].Center);
            }
            var cap = Enclose(items, first, count, bounds);
            if (count <= 6)
            {
                nodes[slot] = new(cap, first, count, nodes.Count);
                return;
            }
            int split = Split(items, first, count, centers, owners);
            Partition(first, split - first);
            Partition(split, first + count - split);
            var left = nodes[slot + 1];
            var right = nodes[left.Escape];
            // For an isotropic great circle on S3 the probability of intersecting a
            // positive cap is sin(alpha)^2. Keep one leaf when visiting both caps
            // costs more than the triangle tests they are expected to eliminate.
            if (left.Count != 0 && right.Count != 0 &&
                (count - 2) * HitProbability(cap) <=
                left.Count * HitProbability(left.Cap) + right.Count * HitProbability(right.Cap))
            {
                nodes.RemoveRange(slot + 1, 2);
                nodes[slot] = new(cap, first, count, slot + 1);
            }
            else nodes[slot] = new(cap, 0, 0, nodes.Count);
        }
    }

    private static double HitProbability(SphericalCap cap) => cap.CosAlpha > 0 ? 1 - cap.CosAlpha * cap.CosAlpha : 1;

    // A positive cap containing all corners contains their normalized positive combinations.
    // Compute it directly to avoid accumulating the slack of nested enclosing spheres.
    private static SphericalCap Enclose(Item[] items, int first, int count, Hull bounds)
    {
        Vector4 mid = bounds.Min + bounds.Max;
        D4 center = new(mid.X, mid.Y, mid.Z, mid.W);
        if (center.Length < 1e-10) return SphericalCap.Full;
        center = center.Unit();
        double k = 1;
        for (int i = first; i < first + count; i++)
        {
            var t = items[i].Triangle;
            k = Math.Min(k, Math.Min(center.Dot(t.A.V), Math.Min(center.Dot(t.B.V), center.Dot(t.C.V))));
        }
        return k > 0 ? new(center, k - 2e-14) : SphericalCap.Full;
    }

    private static int Split<TPermutation>(Item[] items, int first, int count, Hull centers, TPermutation owners)
        where TPermutation : struct, IPermutation
    {
        Span<Bin> bins = stackalloc Bin[BinCount];
        Span<float> rightCost = stackalloc float[BinCount];
        float best = float.PositiveInfinity, threshold = 0;
        int bestAxis = -1;
        for (int axis = 0; axis < 4; axis++)
        {
            float min = centers.Min[axis], width = centers.Max[axis] - min;
            if (width < 1e-9f) continue;
            bins.Clear();
            float scale = BinCount / width;
            for (int i = first; i < first + count; i++)
            {
                int bin = Math.Clamp((int)((items[i].Center[axis] - min) * scale), 0, BinCount - 1);
                bins[bin].Add(items[i].Bounds, 1);
            }
            Bin right = default, left = default;
            for (int b = BinCount - 1; b > 0; b--)
            {
                right.Add(bins[b].Bounds, bins[b].Count);
                rightCost[b] = right.Cost;
            }
            for (int b = 1; b < BinCount; b++)
            {
                left.Add(bins[b - 1].Bounds, bins[b - 1].Count);
                float cost = left.Cost + rightCost[b];
                if (cost >= best) continue;
                best = cost;
                bestAxis = axis;
                threshold = min + b / scale;
            }
        }
        int split = first;
        if (bestAxis >= 0)
        {
            for (int i = first; i < first + count; i++)
            {
                if (items[i].Center[bestAxis] >= threshold) continue;
                (items[i], items[split]) = (items[split], items[i]);
                owners.Swap(i, split);
                split++;
            }
        }
        return split > first && split < first + count ? split : first + count / 2;
    }
}
