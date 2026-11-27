#nullable enable
#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private readonly List<GlomeViewChange> _actionChanges = [];

    // Record the property in the native history itself. Undo/redo can happen after the
    // inspector has switched to another object. Store IDs so history never roots a script.
    internal void TrackProperty(EditorUndoRedoManager undo, GodotObject target, string property)
    {
        if (_space == null) return;
        undo.AddDoMethod(plugin, nameof(plugin.PreviewPropertyChanged), target.GetInstanceId(), property);
        undo.AddUndoMethod(plugin, nameof(plugin.PreviewPropertyChanged), target.GetInstanceId(), property);
    }

    internal void PropertyChanged(ulong id, string property)
    {
        if (_bake != null && GodotObject.InstanceFromId(id) is { } target)
            _actionChanges.Add(new(_bake.Revision, target, property));
    }

    internal void HistoryChanged()
    {
        bool applied = !_needsBake && _baking == null && _space != null && _renderer != null &&
            viewData != null && _actionChanges.Count > 0;
        foreach (var change in _actionChanges)
            applied = applied && _activeProvider!.TryApplyChange(_space!, viewData!, change, _renderer!);
        _actionChanges.Clear();
        if (applied)
        {
            dirty = true;
            overlay.QueueRedraw();
        }
        else RequestBake();
    }
}
#endif
