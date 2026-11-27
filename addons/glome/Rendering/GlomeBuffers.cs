#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Glome.Core;
using Godot;

namespace Glome;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MaterialRecord
{
    public Vector4 BaseColor, EmissionRoughness;
    public float Metallic, AlphaCutoff;
    public uint AlbedoRef, Flags;

    public const uint Unshaded = 1, Nearest = 8, Clamp = 16, DisableMips = 32;
    public const uint NoTexture = uint.MaxValue;
    public static MaterialRecord White => new()
    {
        BaseColor = Vector4.One, EmissionRoughness = new(0, 0, 0, 1), AlbedoRef = NoTexture
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EnvironmentBlock
{
    public Vector4 Background, AmbientEnergy, FogDensity;
    public Vector4I ModeGroundMaterial;
    public Vector4 GroundUvTransform;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LightRecord
{
    public Vector4 Source, RadianceCutoff;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CameraPush
{
    public Vector4 Right, Up, Back, Pos, Lens;
    public Vector4I Dims;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TriangleRecord
{
    public Vector4 Plane, Dual0, Dual1, Dual2, InvKMaterial, Uv01, Uv2, N0, N1, N2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CapRecord
{
    public Vector4 Center, Bounds;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ProbeRecord
{
    public Vector4 Pos, Direction;
    public int Kind, Id;
    public float Angle, U, V, Pad0, Pad1, Pad2;
}

public sealed record TextureBank(int Width, int Height, int MipCount, byte[][] Layers, bool Srgb = false)
{
    // Opt in only when each pixel layer remains immutable across rebuilds.
    public bool Reusable { get; init; }
    internal static TextureBank Empty { get; } = new(1, 1, 1, [[255, 255, 255, 255]]) { Reusable = true };
}

public sealed record GlomeBuffers(byte[] Triangles, byte[] Nodes, byte[] Materials, byte[] Lights,
                                  EnvironmentBlock Environment, TextureBank[] Banks)
{
    internal GlomeBakeTimings? Timings { get; init; }

    static GlomeBuffers()
    {
        Check<CameraPush>(96, 0,16,32,48,64,80);
        Check<TriangleRecord>(160, 0,16,32,48,64,80,96,112,128,144);
        Check<CapRecord>(32, 0,16);
        Check<ProbeRecord>(64, 0,16,32,36,40,44,48,52,56,60);
        Check<MaterialRecord>(48, 0,16,32,36,40,44);
        Check<LightRecord>(32, 0,16);
        Check<EnvironmentBlock>(80, 0,16,32,48,64);
    }

    private static void Check<T>(int stride, params int[] offsets) where T : unmanaged
    {
        var fields = typeof(T).GetFields().Where(f => !f.IsStatic).ToArray();
        if (Marshal.SizeOf<T>() != stride || fields.Length != offsets.Length ||
            fields.Where((field, i) => Marshal.OffsetOf<T>(field.Name).ToInt32() != offsets[i]).Any())
            throw new InvalidOperationException($"Set 0 ABI mismatch in {typeof(T).Name}.");
    }

    public static Vector4 Vec(D4 v) => new((float)v.X, (float)v.Y, (float)v.Z, (float)v.W);
    public static Vector4 Rgba(Color c) => new(c.R, c.G, c.B, c.A);
    public static Vector4 Rgb(Color c, float w) => new(c.R, c.G, c.B, w);
    internal static byte[] Bytes<T>(T value) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();

    public static byte[] PackLights(Vector4I counts, ReadOnlySpan<LightRecord> records)
    {
        if (counts.X < 0 || counts.Y < 0 || counts.Z < 0 || counts.W < 0 ||
            counts.X + counts.Y + counts.Z + counts.W != records.Length)
            throw new ArgumentException("Light counts must match the contiguous record groups.");
        var bytes = new byte[16 + records.Length * 32];
        MemoryMarshal.Write(bytes, in counts);
        MemoryMarshal.AsBytes(records).CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    public static GlomeBuffers Pack(S3BakeSnapshot scene) => Pack(scene, GlomeLight.Pack(scene.Lights));

    internal static GlomeBuffers Pack(S3BakeSnapshot scene, byte[] lights)
    {
        var materials = scene.Materials ?? throw new ArgumentException("A custom material resolver must supply its own table.");
        return Pack(scene.Geometry, materials.Records, lights, scene.Environment, materials.Banks);
    }

    public static GlomeBuffers Pack(BakedGeometry geometry, MaterialRecord[] materials, byte[] lights,
                                    EnvironmentBlock environment, TextureBank[] banks)
    {
        if (materials.Length == 0 || banks.Length > 4) throw new ArgumentException("Invalid material table or bank count.");
        var triangles = new byte[Math.Max(1, geometry.Triangles.Length) * 160];
        var faces = MemoryMarshal.Cast<byte, TriangleRecord>(triangles.AsSpan());
        for (int i = 0; i < geometry.Triangles.Length; i++)
        {
            var t = geometry.Triangles[i];
            faces[i] = new()
            {
                Plane = Vec(t.Plane), Dual0 = Vec(t.Dual0), Dual1 = Vec(t.Dual1), Dual2 = Vec(t.Dual2),
                InvKMaterial = new((float)(1/t.A.K), (float)(1/t.B.K), (float)(1/t.C.K), t.Material),
                Uv01 = new((float)t.A.Uv.X, (float)t.A.Uv.Y, (float)t.B.Uv.X, (float)t.B.Uv.Y),
                Uv2 = new((float)t.C.Uv.X, (float)t.C.Uv.Y, t.Surface, (int)t.Traversal),
                N0 = Vec(t.A.Normal), N1 = Vec(t.B.Normal), N2 = Vec(t.C.Normal)
            };
        }
        var nodes = new byte[Math.Max(1, geometry.Nodes.Length) * 32];
        var caps = MemoryMarshal.Cast<byte, CapRecord>(nodes.AsSpan());
        for (int i = 0; i < geometry.Nodes.Length; i++)
        {
            var n = geometry.Nodes[i];
            caps[i] = new() { Center = Vec(n.Cap.Center), Bounds = new((float)n.Cap.CosAlpha, n.First, n.Count, n.Escape) };
        }
        return new(triangles, nodes, MemoryMarshal.AsBytes(materials.AsSpan()).ToArray(), lights, environment, banks);
    }
}
