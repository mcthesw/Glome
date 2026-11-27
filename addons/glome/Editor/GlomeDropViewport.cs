#nullable enable
#if TOOLS
using System;
using System.Linq;
using Godot;

namespace Glome.Editor;

[Tool]
internal partial class GlomeDropViewport : TextureRect
{
    internal Func<Vector2, string[], bool>? CanPlace;
    internal Action<Vector2, string[]>? Place;

    private static string[] Files(Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary) return [];
        var dict = data.AsGodotDictionary();
        if (!dict.TryGetValue("type", out var type) || type.AsString() != "files" ||
            !dict.TryGetValue("files", out var files)) return [];
        var paths = files.VariantType == Variant.Type.Array
            ? files.AsGodotArray().Select(v => v.AsString()).ToArray() : files.AsStringArray();
        return paths.Length > 0 && paths.All(p => p.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            && ResourceLoader.Exists(p, "PackedScene")) ? paths : [];
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        Files(data) is { Length: > 0 } files && CanPlace?.Invoke(atPosition, files) == true;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (Files(data) is { Length: > 0 } files && CanPlace?.Invoke(atPosition, files) == true)
            Place?.Invoke(atPosition, files);
    }
}
#endif
