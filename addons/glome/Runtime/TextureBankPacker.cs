#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;

namespace Glome;

internal sealed class TextureBankPacker
{
    private sealed class Source(Texture2D texture)
    {
        public readonly Texture2D Texture = texture;
        public readonly Vector2I Extent = new(texture.GetWidth(), texture.GetHeight());
        public int Uses;
        public uint Reference;
    }

    private readonly Dictionary<Texture2D, Source> _sources = [];
    private static readonly float[] Linear = Enumerable.Range(0, 256)
        .Select(i => new Color(i / 255f, 0, 0).SrgbToLinear().R).ToArray();

    public void Use(Texture2D texture)
    {
        if (!_sources.TryGetValue(texture, out var source))
        {
            // The dummy display has no pixel backing for some Texture2D resources.
            // Keep the mesh, material factors and collision available to CPU-only hosts.
            if (DisplayServer.GetName() == "headless")
            {
                using var pixels = texture.GetImage();
                if (pixels == null) return;
            }
            _sources.Add(texture, source = new(texture));
        }
        source.Uses++;
    }

    public uint Reference(Texture2D? texture) => texture != null && _sources.TryGetValue(texture, out var source)
        ? source.Reference : MaterialRecord.NoTexture;

    public TextureBank[] Pack()
    {
        var groups = _sources.Values.GroupBy(s => s.Extent)
            .OrderByDescending(g => g.Sum(s => s.Uses)).ThenBy(g => g.Key.X).ThenBy(g => g.Key.Y).ToArray();
        var banks = new List<TextureBank>(4);
        int native = groups.Length <= 4 ? groups.Length : 3;
        for (int i = 0; i < native; i++) Add(groups[i].Key, groups[i].ToArray());
        if (groups.Length > 4)
        {
            var rest = groups.Skip(3).SelectMany(g => g).ToArray();
            var size = new Vector2I(rest.Max(s => s.Extent.X), rest.Max(s => s.Extent.Y));
            string names = string.Join(", ", rest.Select(s => $"{Label(s.Texture)} ({s.Extent.X}x{s.Extent.Y})"));
            GD.Print($"Glome texture bank 3 packs {names} at {size.X}x{size.Y}.");
            Add(size, rest);
        }
        return banks.ToArray();

        void Add(Vector2I size, Source[] sources)
        {
            var layers = new byte[sources.Length][];
            for (int i = 0; i < layers.Length; i++)
            {
                sources[i].Reference = ((uint)banks.Count << 24) | (uint)i;
                layers[i] = BuildLayer(sources[i].Texture, size);
            }
            banks.Add(new(size.X, size.Y, 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(size.X, size.Y)), layers, Srgb: true)
                { Reusable = true });
        }
    }

    internal static string Label(Resource resource) => !string.IsNullOrEmpty(resource.ResourcePath) ? resource.ResourcePath :
        !string.IsNullOrEmpty(resource.ResourceName) ? resource.ResourceName : $"{resource.GetClass()} #{resource.GetInstanceId()}";

    private static byte[] BuildLayer(Texture2D texture, Vector2I size)
    {
        using var source = texture.GetImage() ?? throw new InvalidOperationException($"No pixels for {Label(texture)}.");
        if (source.IsCompressed() && source.Decompress() != Error.Ok)
            throw new InvalidOperationException($"Cannot decompress {Label(texture)}.");
        source.ClearMipmaps();
        source.Convert(Image.Format.Rgba8);
        byte[] pixels = source.GetData();
        var rgba = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            rgba[i] = Linear[pixels[i]];
            rgba[i+1] = Linear[pixels[i+1]];
            rgba[i+2] = Linear[pixels[i+2]];
            rgba[i+3] = pixels[i+3] / 255f;
        }
        // Both resampling and the complete mip pyramid operate on linear RGB and straight alpha.
        using var image = Image.CreateFromData(source.GetWidth(), source.GetHeight(), false, Image.Format.Rgbaf,
            MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray());
        if (image.GetSize() != size) image.Resize(size.X, size.Y, Image.Interpolation.Bilinear);
        if (size != Vector2I.One && image.GenerateMipmaps() != Error.Ok)
            throw new InvalidOperationException($"Cannot generate mipmaps for {Label(texture)}.");
        var linear = MemoryMarshal.Cast<byte, float>(image.GetData().AsSpan());
        var encoded = new byte[linear.Length];
        int start = 0;
        if (source.GetSize() == size)
        {
            pixels.CopyTo(encoded, 0);
            start = pixels.Length;
        }
        for (int i = start; i < linear.Length; i += 4)
        {
            var c = new Color(linear[i], linear[i+1], linear[i+2], linear[i+3]).LinearToSrgb();
            encoded[i] = (byte)c.R8;
            encoded[i+1] = (byte)c.G8;
            encoded[i+2] = (byte)c.B8;
            encoded[i+3] = (byte)c.A8;
        }
        return encoded;
    }
}
