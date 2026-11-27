#nullable enable
#if TOOLS
using System;
using System.Linq;
using Glome;
using Glome.Core;
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

    // Project a point on the authored box's X handle or roll ring independently of hit testing.
    public Vector2 HandlePoint(MeshInstance3D mesh, Vector2 panel, bool roll)
    {
        if (!_plugin.TryGetViewState(out var space, out var eye, out _, out _, out double fov, out _))
            throw new InvalidOperationException("No presented view");
        double radius = space!.Radius;
        var (frame, pose) = mesh.GetParent<S3Anchor3D>().ChartPose(radius);
        var pivot = ChartCamera.FromTangent(frame, pose * mesh.Transform, radius);
        var axis = roll ? (pivot.Right + pivot.Up).Unit() : pivot.Right;
        var point = pivot.Travel(axis, 1 / radius).P;
        double scale = panel.Y / (-2 * point.Dot(eye.Back) * Math.Tan(double.DegreesToRadians(fov) / 2));
        return panel / 2 + new Vector2((float)(scale * point.Dot(eye.Right)), -(float)(scale * point.Dot(eye.Up)));
    }

    public bool HasNewWireEdges(Image before, Image during)
    {
        if (before.GetSize() != during.GetSize()) return false;
        before.Convert(Image.Format.Rgb8);
        during.Convert(Image.Format.Rgb8);
        var a = before.GetData();
        var b = during.GetData();
        int pixels = 0;
        for (int i = 0; i < b.Length; i += 3)
        {
            // The mesh preview draws white edges. Ignore colored tool axes and the unchanged scene.
            if (Math.Min(b[i], Math.Min(b[i + 1], b[i + 2])) > 232 &&
                Math.Min(a[i], Math.Min(a[i + 1], a[i + 2])) < 200)
                pixels++;
        }
        return pixels > 40;
    }

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
