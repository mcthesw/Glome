#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private bool DisplayRay(Vector2 position, out ArcRay ray)
    {
        ray = default;
        var size = ImageSize;
        if (displayedBake == null || size.X <= 0 || size.Y <= 0) return false;
        var uv = (position - (_image.Size-size)/2)/size;
        if (uv.X < 0 || uv.X >= 1 || uv.Y < 0 || uv.Y >= 1) return false;
        double x = (2*(Math.Floor(uv.X*displayedSize.X)+.5)/displayedSize.X-1)*TanHalfFov*displayedSize.Aspect();
        double y = (1-2*(Math.Floor(uv.Y*displayedSize.Y)+.5)/displayedSize.Y)*TanHalfFov;
        ray = new(displayed.P, displayed.Local(x,y,-1).Unit());
        return true;
    }

    private void SelectPicked(Node? source)
    {
        var target = GlomeNodeEditing.EditableTarget(source, plugin.GetTree().EditedSceneRoot);
        var selection = EditorInterface.Singleton.GetSelection();
        syncingSelection = true;
        try
        {
            selection.Clear();
            if (target != null && GlomeChart.SpaceOf(target) == _space)
            {
                selection.AddNode(target);
                EditorInterface.Singleton.InspectObject(target);
            }
        }
        finally { syncingSelection = false; }
        SelectionChanged();
    }

    private void Pick(Vector2 position)
    {
        if (PickDevice(position) || PickAnchorPin(position) || !DisplayRay(position, out var ray)) return;
        var snapshot = displayedBake!;
        var hit = snapshot.Geometry.Trace(ray);
        if (hit.Kind != HitKind.Miss) _pivot = ray.At(hit.T);
        else VirtualPivot();
        UpdateDistance();
        Node? source = null;
        if (hit.Kind == HitKind.Triangle)
            source = snapshot.AuthoringSources is { } sources
                ? sources.SourceForTriangle(hit.Triangle) : snapshot.TriangleOwners?[hit.Triangle];
        SelectPicked(source);
    }

    private bool CanPlace(Vector2 position, string[] paths)
    {
        var root = plugin.GetTree().EditedSceneRoot;
        return _space != null && root != null && (_space == root || GlomeNodeEditing.EditableTarget(_space, root) == _space) &&
            displayedBake == _bake && DisplayRay(position, out _) && !paths.Contains(root.SceneFilePath);
    }

    private void Place(Vector2 position, string[] paths)
    {
        if (!DisplayRay(position, out var ray)) return;
        var scene = plugin.GetTree().EditedSceneRoot;
        var target = GlomeScenePlacement.Surface(ray, displayedBake!.Geometry.Trace(ray), displayedBake, displayed);
        var chart = GlomeAuthoring.AnchorOf(Selected);
        if (chart != null && (chart.SceneFilePath.Length != 0 && !scene.IsEditableInstance(chart) ||
            GlomeNodeEditing.EditableTarget(chart, scene) != chart)) chart = null;
        List<GlomeInsertion> insertions = [];
        foreach (string path in paths)
        {
            var packed = ResourceLoader.Load<PackedScene>(path);
            if (packed == null || GlomeScenePlacement.Instance(packed, _space!, chart, target) is not { } insertion)
            {
                foreach (var item in insertions) item.Root.Free();
                Message("Place a 3D scene inside the selected chart's forward hemisphere, or clear the selection.");
                return;
            }
            insertions.Add(insertion);
        }
        Insert("Place S³ scene", insertions);
    }

    private void Insert(string title, IReadOnlyList<GlomeInsertion> insertions)
    {
        GlomeScenePlacement.Insert(plugin.GetUndoRedo(), EditorInterface.Singleton.GetSelection(),
            plugin.GetTree().EditedSceneRoot, title, insertions);
        _image.GrabFocus();
    }
}
#endif
