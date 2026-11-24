#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Glome.Core;
using Godot;

namespace Glome;

public sealed record GlomeMaterials(MaterialRecord[] Records, TextureBank[] Banks)
{
    // Match the base-level coverage sampler used by both primary and shadow rays.
    internal bool Covers(int material, D2 uv)
    {
        var record = Records[material];
        float alpha = record.BaseColor.W;
        if (record.AlbedoRef != MaterialRecord.NoTexture)
        {
            var bank = Banks[record.AlbedoRef >> 24];
            var bytes = bank.Layers[record.AlbedoRef & 0x00ffffff];
            bool clamp = (record.Flags & MaterialRecord.Clamp) != 0;
            static int Address(int i, int size, bool clamp) =>
                clamp ? Math.Clamp(i, 0, size - 1) : ((i % size) + size) % size;
            float Texel(int x, int y) => bytes[4*(Address(y, bank.Height, clamp)*bank.Width + Address(x, bank.Width, clamp))+3] / 255f;
            float u = (float)uv.X * bank.Width, v = (float)uv.Y * bank.Height;
            if ((record.Flags & MaterialRecord.Nearest) != 0)
                alpha *= Texel((int)MathF.Floor(u), (int)MathF.Floor(v));
            else
            {
                u -= 0.5f; v -= 0.5f;
                int x = (int)MathF.Floor(u), y = (int)MathF.Floor(v);
                float a = Mathf.Lerp(Texel(x,y), Texel(x+1,y), u-x);
                float b = Mathf.Lerp(Texel(x,y+1), Texel(x+1,y+1), u-x);
                alpha *= Mathf.Lerp(a, b, v-y);
            }
        }
        return alpha >= record.AlphaCutoff;
    }
}

internal sealed class NativeMaterialTable
{
    private static readonly ConditionalWeakTable<Resource, object> Warned = new();
    private readonly Dictionary<StandardMaterial3D, int> _indices = [];
    private readonly List<MaterialRecord> _records = [MaterialRecord.White];
    private readonly TextureBankPacker _textures = new();

    public static void Warn(Resource resource, string feature)
    {
        if (Warned.TryAdd(resource, new object()))
            GD.PushWarning($"Glome ignores {feature} on {TextureBankPacker.Label(resource)}.");
    }

    public SurfaceTag Resolve(Material? source)
    {
        if (source is not StandardMaterial3D m)
        {
            if (source != null) Warn(source, "non-standard material shading");
            return new(0, 0, TraversalFlags.CullBack);
        }
        if (m.AlbedoTexture != null) _textures.Use(m.AlbedoTexture);
        bool cutout = m.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor;
        var traversal = (TraversalFlags)(int)m.CullMode | (cutout ? TraversalFlags.Cutout : 0);
        if (_indices.TryGetValue(m, out int index)) return new(index, 0, traversal);

        var unsupported = new List<string>();
        if (m.RoughnessTexture != null || m.MetallicTexture != null || m.OrmTexture != null || m.AOEnabled)
            unsupported.Add("ORM maps");
        if (m.NormalEnabled) unsupported.Add("normal mapping");
        if (m.EmissionEnabled && m.EmissionTexture != null) unsupported.Add("emission maps");
        if (!cutout && m.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) unsupported.Add("blended transparency");
        if (unsupported.Count != 0) Warn(m, string.Join(", ", unsupported));

        uint flags = (uint)m.CullMode << 1;
        if (m.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) flags |= MaterialRecord.Unshaded;
        if (!m.TextureRepeat) flags |= MaterialRecord.Clamp;
        if (m.TextureFilter is BaseMaterial3D.TextureFilterEnum.Nearest or BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps
            or BaseMaterial3D.TextureFilterEnum.NearestWithMipmapsAnisotropic) flags |= MaterialRecord.Nearest;
        if (m.TextureFilter is BaseMaterial3D.TextureFilterEnum.Nearest or BaseMaterial3D.TextureFilterEnum.Linear)
            flags |= MaterialRecord.DisableMips;
        var emission = m.EmissionEnabled ? m.Emission.SrgbToLinear() * m.EmissionEnergyMultiplier : Colors.Black;
        index = _records.Count;
        _indices.Add(m, index);
        _records.Add(new()
        {
            BaseColor = GlomeBuffers.Rgba(m.AlbedoColor.SrgbToLinear()),
            EmissionRoughness = GlomeBuffers.Rgb(emission, m.Roughness),
            Metallic = m.Metallic, AlphaCutoff = cutout ? m.AlphaScissorThreshold : 0,
            Flags = flags, AlbedoRef = MaterialRecord.NoTexture
        });
        return new(index, 0, traversal);
    }

    public GlomeMaterials Finish()
    {
        var banks = _textures.Pack();
        var records = _records.ToArray();
        foreach (var (material, index) in _indices) records[index].AlbedoRef = _textures.Reference(material.AlbedoTexture);
        return new(records, banks);
    }
}
