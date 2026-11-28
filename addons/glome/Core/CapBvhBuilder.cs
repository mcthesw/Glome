#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Glome.Core;

public readonly record struct CapNode(SphericalCap Cap, int First, int Count, int Escape);

public static class CapBvhBuilder
{
    private const int BinCount = 12;
    private const int ScanBatch = 2048;
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
        // Only arrays permit concurrent writes to disjoint slices. Preserve the
        // original sequence of owner swaps on the caller thread for other lists.
        return Build(triangles, new Permutation<T>(owners), parallelSubtrees: owners is T[]);
    }
    private static CapNode[] Build<TPermutation>(GeodesicTriangle[] triangles, TPermutation owners, bool parallelSubtrees = true)
        where TPermutation : struct, IPermutation
    {
        if (triangles.Length == 0) return [];
        var items = new Item[triangles.Length];
        if (items.Length < ScanBatch) PrepareItems(triangles, items, 0, items.Length);
        else Parallel.For(0, (items.Length + ScanBatch - 1) / ScanBatch,
            b => PrepareItems(triangles, items, b * ScanBatch, Math.Min(items.Length, (b + 1) * ScanBatch)));
        var nodes = new List<CapNode>(items.Length / 2);
        Partition(0, items.Length, nodes, parallelSubtrees ? BitOperations.Log2((uint)Environment.ProcessorCount) : 0);
        for (int i = 0; i < items.Length; i++) triangles[i] = items[i].Triangle;
        var result = nodes.ToArray();
        for (int i = 0; i < result.Length; i++)
            result[i] = result[i] with { Escape = i + result[i].Escape };
        return result;

        void Partition(int first, int count, List<CapNode> output, int parallelDepth)
        {
            int slot = output.Count;
            output.Add(default);
            Hull bounds = Hull.Empty, centers = Hull.Empty;
            for (int i = first; i < first + count; i++)
            {
                bounds = bounds.Union(items[i].Bounds);
                centers = centers.Add(items[i].Center);
            }
            var cap = Enclose(items, first, count, bounds);
            if (count <= 6)
            {
                output[slot] = new(cap, first, count, 1);
                return;
            }
            int split = Split(items, first, count, centers, owners);
            int leftCount = split - first, rightCount = count - leftCount;
            if (parallelDepth > 0 && count >= 4096 && Math.Min(leftCount, rightCount) >= 512)
            {
                PartitionPair(first, leftCount, rightCount, output, parallelDepth - 1);
            }
            else
            {
                Partition(first, leftCount, output, parallelDepth);
                Partition(split, rightCount, output, parallelDepth);
            }
            var left = output[slot + 1];
            var right = output[slot + 1 + left.Escape];
            // For an isotropic great circle on S3 the probability of intersecting a
            // positive cap is sin(alpha)^2. Keep one leaf when visiting both caps
            // costs more than the triangle tests they are expected to eliminate.
            if (left.Count != 0 && right.Count != 0 &&
                (count - 2) * HitProbability(cap) <=
                left.Count * HitProbability(left.Cap) + right.Count * HitProbability(right.Cap))
            {
                output.RemoveRange(slot + 1, 2);
                output[slot] = new(cap, first, count, 1);
            }
            else output[slot] = new(cap, 0, 0, output.Count - slot);
        }

        void PartitionPair(int first, int leftCount, int rightCount, List<CapNode> output, int depth)
        {
            // Relative escape offsets make each subtree relocatable. Appending in
            // preorder reproduces the serial tree regardless of worker completion order.
            var sibling = new List<CapNode>(rightCount / 2);
            Parallel.Invoke(() => Partition(first, leftCount, output, depth),
                            () => Partition(first + leftCount, rightCount, sibling, depth));
            output.AddRange(sibling);
        }
    }

    private static double HitProbability(SphericalCap cap) => cap.CosAlpha > 0 ? 1 - cap.CosAlpha * cap.CosAlpha : 1;

    private static void PrepareItems(GeodesicTriangle[] triangles, Item[] items, int first, int end)
    {
        static Vector4 Point(D4 p) => new((float)p.X, (float)p.Y, (float)p.Z, (float)p.W);
        for (int i = first; i < end; i++)
        {
            var t = triangles[i];
            var a = Point(t.A.V);
            items[i] = new(t, Point(t.Cap.Center), new Hull(a, a).Add(Point(t.B.V)).Add(Point(t.C.V)));
        }
    }

    // A positive cap containing all corners contains their normalized positive combinations.
    // Compute it directly to avoid accumulating the slack of nested enclosing spheres.
    private static SphericalCap Enclose(Item[] items, int first, int count, Hull bounds)
    {
        Vector4 mid = bounds.Min + bounds.Max;
        D4 center = new(mid.X, mid.Y, mid.Z, mid.W);
        if (center.Length < 1e-10) return SphericalCap.Full;
        center = center.Unit();
        double k = count < 8 * ScanBatch ? CornerMinimum(items, center, first, first + count)
            : ParallelCornerMinimum(items, center, first, count);
        return k > 0 ? new(center, k - 2e-14) : SphericalCap.Full;
    }

    private static double ParallelCornerMinimum(Item[] items, D4 center, int first, int count)
    {
        var minima = new double[(count + ScanBatch - 1) / ScanBatch];
        Parallel.For(0, minima.Length, b => minima[b] = CornerMinimum(items, center, first + b * ScanBatch,
            first + Math.Min((b + 1) * ScanBatch, count)));
        double k = 1;
        foreach (double minimum in minima) k = Math.Min(k, minimum);
        return k;
    }

    private static double CornerMinimum(Item[] items, D4 center, int first, int end)
    {
        double k = 1;
        for (int i = first; i < end; i++)
        {
            var t = items[i].Triangle;
            k = Math.Min(k, Math.Min(center.Dot(t.A.V), Math.Min(center.Dot(t.B.V), center.Dot(t.C.V))));
        }
        return k;
    }

    private static int Split<TPermutation>(Item[] items, int first, int count, Hull centers, TPermutation owners)
        where TPermutation : struct, IPermutation
    {
        // Bin the four candidate axes in one pass over the triangle bounds.
        Span<Bin> allBins = stackalloc Bin[4 * BinCount];
        allBins.Clear();
        var widths = centers.Max - centers.Min;
        var scales = new Vector4(BinCount) / Vector4.Max(widths, new Vector4(1e-9f));
        if (count < 8 * ScanBatch) BinItems(items, first, first + count, centers.Min, scales, allBins);
        else ParallelBins(items, first, count, centers.Min, scales, allBins);
        Span<float> rightCost = stackalloc float[BinCount];
        float best = float.PositiveInfinity, threshold = 0;
        int bestAxis = -1;
        for (int axis = 0; axis < 4; axis++)
        {
            if (widths[axis] < 1e-9f) continue;
            var bins = allBins.Slice(axis * BinCount, BinCount);
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
                threshold = centers.Min[axis] + b / scales[axis];
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

    private static void ParallelBins(Item[] items, int first, int count, Vector4 min, Vector4 scales, Span<Bin> bins)
    {
        int batches = (count + ScanBatch - 1) / ScanBatch;
        var partial = new Bin[batches * 4 * BinCount];
        Parallel.For(0, batches, b => BinItems(items, first + b * ScanBatch,
            first + Math.Min((b + 1) * ScanBatch, count), min, scales,
            partial.AsSpan(b * 4 * BinCount, 4 * BinCount)));
        for (int start = 0; start < partial.Length; start += bins.Length)
        for (int j = 0; j < bins.Length; j++)
            bins[j].Add(partial[start + j].Bounds, partial[start + j].Count);
    }

    private static void BinItems(Item[] items, int first, int end, Vector4 min, Vector4 scales, Span<Bin> bins)
    {
        var last = new Vector4(BinCount - 1);
        for (int i = first; i < end; i++)
        {
            ref readonly var item = ref items[i];
            var bin = Vector4.Clamp((item.Center - min) * scales, Vector4.Zero, last);
            bins[(int)bin.X].Add(item.Bounds, 1);
            bins[BinCount + (int)bin.Y].Add(item.Bounds, 1);
            bins[2 * BinCount + (int)bin.Z].Add(item.Bounds, 1);
            bins[3 * BinCount + (int)bin.W].Add(item.Bounds, 1);
        }
    }
}
