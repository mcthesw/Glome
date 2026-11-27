#nullable enable
#if TOOLS
using System;
using System.Linq;
using Godot;
using Glome.Core;
using Dict = Godot.Collections.Dictionary;
using Marks = Godot.Collections.Array<Godot.Collections.Dictionary>;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private string sceneKey = "";
    private Marks marks = new();
    private readonly System.Collections.Generic.Dictionary<string, Dict> pending = new();
    private int chosen = -1;

    private Dict CameraState()
    {
        D4[] axes = [_horizon.Right, _horizon.Up, _horizon.Back, _horizon.P];
        return new Dict {
            ["horizon"] = axes.SelectMany(a => new[] { a.X, a.Y, a.Z, a.W }).ToArray(),
            ["view"] = Provider.Id, ["pitch"] = _pitch, ["pivot"] = new[] { _pivot.X, _pivot.Y, _pivot.Z, _pivot.W },
            ["pivotDist"] = _pivotDist
        };
    }

    private void SetCamera(Dict camera)
    {
        _focus = null;
        if (camera.TryGetValue("view", out var view) && Provider.Id != view.AsString()) SelectView(view.AsString());
        var h = camera["horizon"].AsFloat64Array();
        var c = camera["pivot"].AsFloat64Array();
        if (h.Length != 16 || c.Length != 4) return;
        D4 Axis(int i) => new(h[i], h[i+1], h[i+2], h[i+3]);
        _horizon = new(Axis(0), Axis(4), Axis(8), Axis(12));
        polar = Math.Abs(_horizon.P.Y) > .992;
        _pitch = camera["pitch"].AsDouble();
        _pivot = new(c[0], c[1], c[2], c[3]);
        _pivotDist = camera["pivotDist"].AsDouble();
    }

    public Dict State()
    {
        if (pending.TryGetValue(sceneKey, out var buffered)) return buffered;
        if (_bake == null) return new();
        var state = CameraState();
        state["version"] = 2;
        state["scene"] = sceneKey;
        state["speed"] = speed;
        state["bookmarks"] = marks.Duplicate(true);
        return state;
    }

    public void QueueState(Dict state)
    {
        if (!state.ContainsKey("version") || state["version"].AsInt32() is not (1 or 2)) return;
        string key = state["scene"].AsString();
        if (sceneKey == key && _bake != null) Restore(state);
        else pending[key] = state.Duplicate(true);
    }

    private void Restore(Dict state)
    {
        SetCamera(state);
        SetSpeed(state["speed"].AsDouble());
        if (state.TryGetValue("mode", out var legacy))
        {
            // Version 1 had Clean at index zero and a single optional extension at one.
            SelectView(legacy.AsInt32() == 1 && providers.Length > 3 ? providers[3].Id : "ClayClean");
        }
        marks = state["bookmarks"].AsGodotArray<Dict>().Duplicate(true);
        chosen = -1;
    }

    private void AddBookmarks(Container toolbar)
    {
        var menu = new MenuButton { Text = "Bookmarks" };
        toolbar.AddChild(menu);
        var popup = menu.GetPopup();
        Listen(menu, MenuButton.SignalName.AboutToPopup, Callable.From(() =>
        {
            popup.Clear();
            popup.AddItem("Save Current View...", 0);
            for (int i = 0; i < marks.Count; i++) popup.AddItem(marks[i]["name"].AsString(), 3+i);
            popup.AddSeparator();
            popup.AddItem("Rename...", 1);
            popup.AddItem("Delete...", 2);
            foreach (int id in new[] { 1, 2 })
                popup.SetItemDisabled(popup.GetItemIndex(id), chosen < 0 || chosen >= marks.Count);
        }));
        var dialog = new ConfirmationDialog();
        var field = new LineEdit();
        dialog.AddChild(field);
        panel.AddChild(dialog);
        Marks? target = null;
        Dict? camera = null;
        int index = -1;
        Listen(dialog, AcceptDialog.SignalName.Confirmed, Callable.From(() =>
        {
            string name = field.Text.Trim();
            if (target != marks || name.Length == 0) return;
            if (camera != null) { camera["name"] = name; marks.Add(camera); chosen = marks.Count - 1; }
            else if (index >= 0 && index < marks.Count) marks[index]["name"] = name;
        }));
        Listen(popup, PopupMenu.SignalName.IdPressed, Callable.From<long>(id =>
        {
            if (_bake == null) return;
            if (id >= 3) { chosen = (int)id-3; SetCamera(marks[chosen]); return; }
            if (id == 2) { marks.RemoveAt(chosen); chosen = -1; return; }
            target = marks;
            index = chosen;
            camera = id == 0 ? CameraState() : null;
            dialog.Title = id == 0 ? "Save Current View" : "Rename Bookmark";
            field.Text = id == 0 ? $"View {marks.Count + 1}" : marks[chosen]["name"].AsString();
            dialog.PopupCentered(new Vector2I(320, 110));
            field.GrabFocus();
            field.SelectAll();
        }));
    }
}
#endif
