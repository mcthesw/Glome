#nullable enable
#if TOOLS
using System;
using System.Threading.Tasks;
using Glome.Core;
using Godot;

namespace Glome.Editor;

[Tool]
public partial class GlomeEditorPlugin : EditorPlugin, ISerializationListener
{
    private GlomeEditorSession? session;
    private GlomeEditorBridge? bridge;
    private GlomeInspectorPlugin? inspector;
    private bool visible;
    private int generation;
    /// <summary>Local bridge directory. Defaults to GLOME_BRIDGE_DIR or glome-bridge in the project user data directory.</summary>
    public static string? BridgeDirectory { get; set; }
    public static Func<GlomeEditorPlugin, Task<(bool Ok, string Message)>>? BridgeShaderReload { get; set; }
    private Godot.Collections.Dictionary deferredState = new();
    public override Godot.Collections.Dictionary _GetState() => session?.State() ?? deferredState;
    public override void _SetState(Godot.Collections.Dictionary state)
    {
        if (session == null) deferredState = state.Duplicate(true);
        else session.QueueState(state);
    }
    public override bool _HasMainScreen() => true;
    public override string _GetPluginName() => "S³";
    public override Texture2D _GetPluginIcon()
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        return theme.GetIcon(theme.HasIcon("SphereMesh", "EditorIcons") ? "SphereMesh" : "Node3D", "EditorIcons");
    }
    public override void _MakeVisible(bool value) { visible = value; session?.Visible(value); }
    public override void _EnterTree() => Open();
    private void Open()
    {
        if (!IsInsideTree() || !Engine.IsEditorHint() || DisplayServer.GetName() == "headless") return;
        if (session != null) return;
        inspector = new() { Undo = GetUndoRedo() };
        AddInspectorPlugin(inspector);
        session = new(this);
        session.QueueState(deferredState);
        deferredState = new();
        session.Visible(visible);
        bridge = new(this);
    }
    public bool TryGetViewState(out S3Space3D? space, out S3Frame view, out D4 pivot,
                                out double pivotDist, out double fov, out string viewMode)
    {
        if (session is not null) return session.TryGetViewState(out space, out view, out pivot, out pivotDist, out fov, out viewMode);
        space = null;
        view = S3Frame.Identity;
        pivot = default;
        pivotDist = fov = 0;
        viewMode = "";
        return false;
    }
    public bool SetView(S3Frame view, D4? pivot = null) => session?.SetView(view, pivot) == true;
    public bool FocusNode(Node3D node) => session?.FocusNode(node) == true;
    public bool RequestRebake() => session?.RequestBake() == true;
    public Task<bool> CapturePreviewImage(string outputPath) =>
        session?.CapturePreviewImage(outputPath) ?? Task.FromResult(false);
    public Task<bool> ReloadPreviewShader(RDShaderSpirV spirv) =>
        session?.ReloadPreviewShader(spirv) ?? Task.FromResult(false);
    internal void PreviewSelectionChanged() => session?.SelectionChanged();
    internal void PreviewStopFly() => session?.StopFly();
    internal void PreviewFramePresented() => session?.FramePresented();
    internal void PreviewSceneSwitched(Node? scene) => session?.SceneSwitched(scene);
    internal void PreviewRequestBake() => session?.RequestBake();
    public override void _Process(double delta)
    {
        session?.Process(delta);
        bridge?.Process(delta, BridgeDirectory);
    }
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) session?.StopFly();
    }
    public override void _ExitTree() => OnBeforeSerialize();
    public void OnBeforeSerialize()
    {
        generation++;
        bridge?.Close();
        bridge = null;
        if (session == null) return;
        deferredState = session.State();
        session.Dispose();
        session = null;
        RemoveInspectorPlugin(inspector!);
        inspector = null;
    }
    public void OnAfterDeserialize()
    {
        int expected = generation;
        Callable.From(() => { if (expected == generation) Open(); }).CallDeferred();
    }
}
#endif
