#nullable enable
#if TOOLS
using System;
using System.Linq;
using Glome;
using Glome.Editor;
using Godot;
using Dict = Godot.Collections.Dictionary;

[Tool]
public partial class EditorChecks : RefCounted
{
    private readonly Swatch _view = new();
    private IDisposable? _registration;
    private GlomeEditorPlugin _plugin = null!;
    private Dict _before = null!;

    public void RegisterView()
    {
        var root = EditorInterface.Singleton.GetBaseControl().GetTree().Root;
        _plugin = root.FindChildren("*", "EditorPlugin", true, false).OfType<GlomeEditorPlugin>().Single();
        _before = _plugin._GetState().Duplicate(true);
        var retired = GlomeEditorViews.Register(_view);
        retired.Dispose();
        _registration = GlomeEditorViews.Register(_view);
        retired.Dispose();
    }

    public bool BookmarkHasId()
    {
        var marks = _plugin._GetState()["bookmarks"].AsGodotArray<Dict>();
        return marks.Count > 0 && marks[^1]["view"].AsString() == _view.Id;
    }

    public void RestoreLegacy(int mode)
    {
        var state = _before.Duplicate(true);
        state.Remove("view");
        state["version"] = 1;
        state["mode"] = mode;
        _plugin._SetState(state);
    }

    public void UnregisterView() => _registration?.Dispose();
    public void RestoreState() => _plugin._SetState(_before);

    private sealed class Swatch : IGlomeEditorViewProvider
    {
        public string Id => "sample.swatch";
        public string DisplayName => "Material Swatch";
        public bool AppliesTo(S3Space3D space) => true;
        public GlomeEditorView Bake(S3Space3D space)
        {
            var snapshot = GlomeBaker.Bake(space, includeOwners: true);
            return new(snapshot, GlomeBuffers.Pack(snapshot), space.GetNode<S3Camera3D>("Walker/Eye").EyeFrame(space.Radius));
        }
    }
}
#endif
