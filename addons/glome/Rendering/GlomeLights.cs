#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome;

public enum LightGroup { Directional, ShadowedDirectional, Omni, ShadowedOmni }

public readonly record struct GlomeLight(LightGroup Group, LightRecord Record)
{
    public static GlomeLight Directional(D4 pole, Vector3 radiance, bool shadows) =>
        Create(shadows ? LightGroup.ShadowedDirectional : LightGroup.Directional, pole, radiance, -1);

    public static GlomeLight Omni(D4 position, Vector3 radiance, double metres, double radius, bool shadows) =>
        Create(shadows ? LightGroup.ShadowedOmni : LightGroup.Omni, position, radiance,
            (float)Math.Cos(Math.Clamp(metres/radius, 0, Math.PI)));

    private static GlomeLight Create(LightGroup group, D4 source, Vector3 radiance, float cutoff) =>
        new(group, new() { Source = GlomeBuffers.Vec(source), RadianceCutoff = new(radiance.X,radiance.Y,radiance.Z,cutoff) });

    internal static GlomeLight? FromNode(Light3D light, S3Frame frame, Transform3D pose, double radius)
    {
        Color rgb = light.LightColor.SrgbToLinear() * light.LightEnergy;
        Vector3 radiance = new(rgb.R,rgb.G,rgb.B);
        if (light is DirectionalLight3D)
        {
            var axis = pose.Basis.Z;
            if (axis.IsZeroApprox()) return null;
            return Directional(frame.Local(axis.X,axis.Y,axis.Z).Unit(),radiance,light.ShadowEnabled);
        }
        if (light is not OmniLight3D omni) return null;
        var p = pose.Origin;
        var position = S3ChartMath.CentralProjection(frame,radius,frame.Local(p.X,p.Y,p.Z),out _);
        return Omni(position,radiance,omni.OmniRange,radius,light.ShadowEnabled);
    }

    public static byte[] Pack(IEnumerable<GlomeLight> lights)
    {
        var groups = lights.ToLookup(l => l.Group);
        int Count(LightGroup group) => groups[group].Count();
        var counts = new Vector4I(Count(LightGroup.Directional), Count(LightGroup.ShadowedDirectional),
            Count(LightGroup.Omni), Count(LightGroup.ShadowedOmni));
        var records = Enum.GetValues<LightGroup>().SelectMany(g => groups[g]).Select(l => l.Record).ToArray();
        return GlomeBuffers.PackLights(counts, records);
    }
}
