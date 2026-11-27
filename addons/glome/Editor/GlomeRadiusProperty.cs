#nullable enable
#if TOOLS
using System.Globalization;
using Godot;

namespace Glome.Editor;

// Keep the native slider live while scrubbing. Publishing the final radius once
// also avoids rebuilding procedural layout in a host's exported property setter.
[Tool]
internal partial class GlomeRadiusProperty : EditorProperty, ISerializationListener
{
    private EditorSpinSlider? _slider;
    private bool _dragging;

    internal static GlomeRadiusProperty? CreateFor(string hint)
    {
        var fields = hint.Split(',');
        static bool Number(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        if (fields.Length < 3 || !Number(fields[0], out double min) ||
            !Number(fields[1], out double max) || !Number(fields[2], out double step)) return null;
        string suffix = "";
        for (int i = 3; i < fields.Length; i++)
        {
            if (!fields[i].StartsWith("suffix:", System.StringComparison.Ordinal)) return null;
            suffix = fields[i][7..];
        }
        var property = new GlomeRadiusProperty();
        var slider = new EditorSpinSlider
        {
            Name = "CurvatureRadius", MinValue = min, MaxValue = max, Step = step,
            Suffix = suffix, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        property.AddChild(slider);
        property.AddFocusable(slider);
        return property;
    }

    public override void _EnterTree() => Attach();

    private void Attach()
    {
        if (_slider != null || !IsInsideTree() || GetNodeOrNull<EditorSpinSlider>("CurvatureRadius") is not { } slider) return;
        _slider = slider;
        slider.Grabbed += Grab;
        slider.Ungrabbed += Release;
        slider.ValueChanged += Change;
        slider.ReadOnly = ReadOnly;
        _UpdateProperty();
    }

    public override void _SetReadOnly(bool readOnly)
    {
        if (_slider is { } slider) slider.ReadOnly = readOnly;
    }

    public override void _UpdateProperty()
    {
        if (!_dragging && _slider is { } slider && GetEditedObject() is S3Space3D space)
            slider.SetValueNoSignal(space.Radius);
    }

    private void Grab() => _dragging = true;

    private void Release()
    {
        _dragging = false;
        if (_slider != null) Change(_slider.Value);
    }

    private void Change(double value)
    {
        if (!_dragging && !ReadOnly && GetEditedObject() is S3Space3D space && value != space.Radius)
            EmitChanged(GetEditedProperty(), value);
    }

    public override void _ExitTree() => OnBeforeSerialize();
    public void OnBeforeSerialize()
    {
        if (_slider is { } slider && IsInstanceValid(slider))
        {
            slider.Grabbed -= Grab;
            slider.Ungrabbed -= Release;
            slider.ValueChanged -= Change;
        }
        _slider = null;
        _dragging = false;
    }

    public void OnAfterDeserialize() => CallDeferred(MethodName.Attach);
}
#endif
