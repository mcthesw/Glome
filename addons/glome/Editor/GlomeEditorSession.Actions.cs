#nullable enable
#if TOOLS
using System.Collections.Generic;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private void AddAnchorMenu(Container bar)
    {
        var menu = new MenuButton { Text = "Anchor", FocusMode = Control.FocusModeEnum.None };
        bar.AddChild(menu);
        var popup = menu.GetPopup();
        popup.AddItem("New Anchor Here", 0);
        popup.AddItem("Duplicate at Antipode", 1);
        popup.AddItem("Snap to Ground", 2);
        popup.AddItem("Face Origin", 3);
        Listen(menu, MenuButton.SignalName.AboutToPopup, Callable.From(() =>
        {
            var root = plugin.GetTree().EditedSceneRoot;
            popup.SetItemDisabled(0, _bake == null || _space == null || (_space != root && _space.Owner != root));
            for (int i = 1; i <= 3; i++) popup.SetItemDisabled(i, !Editable);
        }));
        Listen(popup, PopupMenu.SignalName.IdPressed, Callable.From<long>(id =>
        {
            if (_bake == null || _space == null) return;
            if (id == 0)
            {
                var ahead = _view.Travel(-_view.Back, System.Math.Min(4.85 / _space.Radius, 0.4 * System.Math.PI));
                var here = GlomeAuthoring.Level(GlomeAuthoring.Ground(ahead));
                var node = new S3Anchor3D { Name = "Anchor", ChartPlacement = S3Anchor3D.PlacementMode.Origin,
                    RelativeSpin = S3ChartMath.Encode(here) };
                InsertAnchor("New S³ anchor here", _space, node, [node]);
            }
            else if (Editable && selected is { } anchor)
            {
                if (id == 1)
                {
                    var copy = CopyAtAntipode(anchor, plugin.GetTree().EditedSceneRoot, _space.Radius, out var owned);
                    InsertAnchor("Duplicate S³ anchor at antipode", anchor.GetParent(), copy, owned);
                }
                else
                {
                    var from = Pivot(anchor);
                    var to = id == 2 ? GlomeAuthoring.Ground(from) : GlomeAuthoring.FaceOrigin(from);
                    if (to != from) CommitSpin(id == 2 ? "Snap S³ anchor to ground" : "Face origin",
                        anchor, GlomeAuthoring.Between(from, to));
                }
            }
            _image.GrabFocus();
        }));
    }

    internal static S3Anchor3D CopyAtAntipode(S3Anchor3D original, Node scene, double radius, out List<Node> owned)
    {
        var opposite = GlomeAuthoring.Opposite(S3Frame.Identity);
        var copy = (S3Anchor3D)original.Duplicate();
        copy.Name = original.Name + "Antipode";
        List<Node> owners = [copy];
        copy.RelativeSpin = GlomeAuthoring.DisplacedSpin(original, opposite, radius);
        CopyChildren(original, copy, true);
        owned = owners;
        return copy;

        void CopyChildren(Node source, Node target, bool inSpace)
        {
            foreach (var child in source.GetChildren())
            {
                var duplicate = target.GetNodeOrNull(new NodePath(child.Name.ToString()));
                if (duplicate == null) continue;
                // Procedural builders recreate their unowned geometry on tree entry.
                if (child.Owner == null) { duplicate.Free(); continue; }
                if (child.Owner == scene) owners.Add(duplicate);
                bool sameSpace = inSpace && child is not S3Space3D and not Viewport;
                if (sameSpace && child is S3Anchor3D anchor && anchor.ChartPlacement != S3Anchor3D.PlacementMode.ParentChart)
                    ((S3Anchor3D)duplicate).RelativeSpin = GlomeAuthoring.DisplacedSpin(anchor, opposite, radius);
                CopyChildren(child, duplicate, sameSpace);
            }
        }
    }

    private void InsertAnchor(string title, Node parent, S3Anchor3D node, List<Node> owned)
    {
        var root = plugin.GetTree().EditedSceneRoot;
        var selection = EditorInterface.Singleton.GetSelection();
        var undo = plugin.GetUndoRedo();
        undo.CreateAction(title, customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.AddChild, node, true);
        undo.AddDoReference(node);
        foreach (var child in owned) undo.AddDoProperty(child, Node.PropertyName.Owner, root);
        undo.AddDoMethod(selection, EditorSelection.MethodName.Clear);
        undo.AddDoMethod(selection, EditorSelection.MethodName.AddNode, node);
        undo.AddUndoMethod(selection, EditorSelection.MethodName.Clear);
        undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, node);
        foreach (var old in selection.GetSelectedNodes()) undo.AddUndoMethod(selection, EditorSelection.MethodName.AddNode, old);
        FinishAction(undo);
    }

    private void CommitSpin(string title, S3Anchor3D anchor, S3Frame delta)
    {
        var undo = plugin.GetUndoRedo();
        undo.CreateAction(title, customContext: plugin.GetTree().EditedSceneRoot);
        undo.AddDoProperty(anchor, nameof(anchor.RelativeSpin), GlomeAuthoring.DisplacedSpin(anchor, delta, _space!.Radius));
        undo.AddUndoProperty(anchor, nameof(anchor.RelativeSpin), anchor.RelativeSpin);
        FinishAction(undo);
    }

    private void FinishAction(EditorUndoRedoManager undo)
    {
        undo.AddDoMethod(_space!, nameof(S3Space3D.RequestRebake));
        undo.AddUndoMethod(_space!, nameof(S3Space3D.RequestRebake));
        undo.CommitAction();
    }
}
#endif
