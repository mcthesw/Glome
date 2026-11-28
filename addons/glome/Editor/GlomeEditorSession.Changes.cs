#nullable enable
#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private readonly List<GlomeViewChange> _actionChanges = [];

    // CSG has no mesh-changed signal. Native boolean evaluation replaces the root
    // ArrayMesh after a brush edit. Reading its identity does not evaluate the tree.
    private readonly record struct CsgState(ulong Mesh, ulong Override, S3Frame Chart, Transform3D Pose,
        bool Visible, bool Collision)
    {
        internal static CsgState Read(CsgShape3D root, double radius)
        {
            var (frame, pose) = GlomeChart.InheritedPose(root, radius);
            return new(root.BakeStaticMesh()?.GetInstanceId() ?? 0, root.MaterialOverride?.GetInstanceId() ?? 0,
                frame, pose, root.IsVisibleInTree(), root.UseCollision);
        }
    }

    private (CsgShape3D Root, CsgState State)[] _csgRoots = [];
    private double _csgElapsed;

    private void WatchCsgHierarchy()
    {
        var changed = new Callable(plugin, nameof(plugin.PreviewHierarchyChanged));
        Listen(plugin.GetTree(), SceneTree.SignalName.NodeAdded, changed);
        Listen(plugin.GetTree(), SceneTree.SignalName.NodeRemoved, changed);
    }

    internal void HierarchyChanged(Node node)
    {
        if (_space != null && node is CsgShape3D && GlomeChart.SpaceOf(node) == _space) MarkDirty();
    }

    private void RememberCsgRoots() => _csgRoots = GlomeChart.Descendants(_space!).OfType<CsgShape3D>()
        .Where(root => root.IsRootShape()).Select(root => (root, CsgState.Read(root, _space!.Radius))).ToArray();

    private void CheckCsgChanges(double delta)
    {
        if (_csgRoots.Length == 0 || _needsBake || _baking != null || (_csgElapsed += delta) < .1) return;
        _csgElapsed = 0;
        foreach (var (root, state) in _csgRoots)
            if (!GodotObject.IsInstanceValid(root) || !root.IsInsideTree() || state != CsgState.Read(root, _space!.Radius))
            {
                MarkDirty();
                return;
            }
    }

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
