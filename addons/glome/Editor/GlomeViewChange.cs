#nullable enable
#if TOOLS
using System;
using Godot;

namespace Glome.Editor;

public readonly record struct GlomeViewChange(long Revision, GodotObject Target, string Property)
{
    private bool TransformEdit => Property is "position" or "transform" or "rotation" or "rotation_degrees"
        or "quaternion" or "scale" or "basis" or "global_transform" or "global_position" or "global_rotation";

    public bool IsEnvironment(S3Space3D space, S3BakeSnapshot snapshot)
    {
        if (Target != space || Property != nameof(S3Space3D.Environment))
            return Target == space.Environment && Property is
                nameof(S3Environment.AmbientColor) or nameof(S3Environment.AmbientEnergy) or nameof(S3Environment.FogColor)
                or nameof(S3Environment.FogDensity) or nameof(S3Environment.BackgroundColor) or nameof(S3Environment.ShadingMode);

        var env = space.Environment;
        bool ground = space.EquatorialGroundEnabled && (env?.EquatorialGroundEnabled ?? true);
        return snapshot.AuthoringSources is { } sources && ground == (snapshot.Environment.ModeGroundMaterial.Y >= 0) &&
            sources.GroundMaterialId == (env?.GroundMaterial?.GetInstanceId() ?? 0);
    }

    // A moved light can itself parent a mesh, another light, a proxy or a nested chart.
    public bool TryLight(S3Space3D space, GlomeEditorView view, out int slot, out LightRecord record)
    {
        slot = -1;
        record = default;
        if (Revision != view.Snapshot.Revision || space.LastBake != view.Snapshot ||
            Target is not Light3D light || !light.Visible || GlomeChart.SpaceOf(light) != space ||
            view.NativeLightSlots == null || !view.NativeLightSlots.TryGetValue(light.GetInstanceId(), out var binding))
            return false;
        if (TransformEdit)
        {
            if (light.GetChildCount() != 0) return false;
        }
        else if (Property is not ("light_energy" or "light_color" or "omni_range")) return false;

        var (frame, pose) = GlomeChart.InheritedPose(light, space.Radius);
        if (GlomeLight.FromNode(light, frame, pose, space.Radius) is not { } current || current.Group != binding.Group)
            return false;
        (slot, record) = (binding.Slot, current.Record);
        return true;
    }
}
#endif
