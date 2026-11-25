#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Glome.Diagnostics;
using Godot;
using static Glome.Diagnostics.GlomeDebugBridge;

namespace Glome.Editor;

internal sealed class GlomeEditorBridge(GlomeEditorPlugin preview)
{
    private GlomeDebugBridge _files = OpenFiles(null);
    private string? _directory;
    private static EditorInterface Editor => EditorInterface.Singleton;
    private bool _closed;

    private static GlomeDebugBridge OpenFiles(string? directory) =>
        new(Path.GetFullPath(ProjectSettings.GlobalizePath(directory ??
            System.Environment.GetEnvironmentVariable("GLOME_BRIDGE_DIR") ?? Path.Combine(OS.GetUserDataDir(), "glome-bridge"))),
            "editor", "editor-latest", 0.3);

    public void Process(double delta, string? directory)
    {
        if (_files.IsBusy) return;
        if (_directory != directory)
        {
            _files = OpenFiles(directory);
            _directory = directory;
        }
        _files.Poll(delta, Dispatch);
    }

    private async Task<Result> Snapshot()
    {
        await preview.ToSignal(preview.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_closed) return PreviewUnavailable();
        await preview.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return _closed ? PreviewUnavailable() : await _files.Snapshot(BuildSnapshot, preview.CapturePreviewImage);
    }

    private object BuildSnapshot(string? imagePath)
    {
        var root = Editor.GetEditedSceneRoot();
        string? scene = root?.SceneFilePath;
        S3Space3D? space = null;
        string? viewMode = null;
        Dictionary<string, object?>? camera = null;
        if (preview.TryGetViewState(out space, out var view, out _, out double distance, out double fov, out viewMode))
        {
            camera = Camera(view, space!.Radius);
            camera["pivotDistM"] = distance;
            camera["fovDeg"] = fov;
        }
        return new
        {
            source = "editor", timestamp = DateTimeOffset.UtcNow, editedScenePath = scene,
            hasUnsavedChanges = root is not null && (string.IsNullOrEmpty(scene) || Editor.GetUnsavedScenes().Contains(scene)),
            openScenes = Editor.GetOpenScenes(),
            spacePath = space is not null ? root?.GetPathTo(space).ToString() : null, radius = space?.Radius, viewMode, camera,
            selectedNodes = Editor.GetSelection().GetSelectedNodes().Select(node => Describe(node, root)).ToArray(),
            imagePath
        };
    }

    private static object Describe(Node node, Node? root)
    {
        var spatial = node as Node3D;
        var anchor = node as S3Anchor3D;
        return new
        {
            name = node.Name.ToString(), path = (root?.GetPathTo(node) ?? node.GetPath()).ToString(), type = node.GetType().Name,
            transform = spatial is null ? null : new
            {
                position = Vector(spatial.Position), rotationDeg = Vector(spatial.RotationDegrees), scale = Vector(spatial.Scale)
            },
            anchor = anchor is null ? null : new
            {
                placement = anchor.ChartPlacement.ToString(), polarDeg = anchor.PolarDegrees,
                azimuthDeg = anchor.BearingDegrees, elevationDeg = anchor.ElevationDegrees,
                arcMetres = anchor.ArcMetres, localOffset = Vector(anchor.LocalOffset),
                editorChartRadius = anchor.EditorChartRadius, relativeSpin = anchor.RelativeSpin
            }
        };
    }

    private async Task<Result> Dispatch(string command, JsonElement args)
    {
        switch (command)
        {
            case "status": return _files.Status(BuildSnapshot);
            case "snapshot": return await Snapshot();
            case "rebake":
                return preview.RequestRebake() ? new(true, "Preview rebake requested.") : PreviewUnavailable();
            case "reload_shader":
                if (GlomeEditorPlugin.BridgeShaderReload is not { } reload)
                    return new(false, "No preview shader reload handler is registered.");
                var (ok, message) = await reload(preview);
                return new(ok, message);
            case "reload_scene": return ReloadScene(args);
            case "focus_node": return FocusNode(args);
            case "set_camera":
                var request = CameraRequest.Read(args);
                if (!request.HasChanges) return new(false, "Supply camera coordinates.");
                if (!preview.TryGetViewState(out var space, out var view, out _, out _, out _, out _))
                    return PreviewUnavailable();
                return preview.SetView(request.Apply(view, space!.Radius))
                    ? new(true, "Preview camera moved.") : PreviewUnavailable();
            default: return new(false, $"Unknown editor command '{command}'.");
        }
    }

    private Result ReloadScene(JsonElement args)
    {
        string? path = Editor.GetEditedSceneRoot()?.SceneFilePath;
        if (string.IsNullOrEmpty(path)) return new(false, "Save the active scene before reloading it.", UnsavedChanges: true);
        bool force = args.TryGetProperty("force", out var value) && value.GetBoolean();
        if (!force && Editor.GetUnsavedScenes().Contains(path))
            return new(false, "The active scene has unsaved changes.", UnsavedChanges: true);

        Editor.GetResourceFilesystem().Scan();
        Editor.ReloadSceneFromPath(path);
        preview.RequestRebake();
        return new(true, $"Reloaded {path}.");
    }

    private Result FocusNode(JsonElement args)
    {
        var root = Editor.GetEditedSceneRoot();
        if (root is null) return new(false, "Open a scene first.");
        var path = StringArg(args, "path");
        var name = StringArg(args, "name");
        Node? node;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (path.StartsWith('/') || path.Split('/').Contains(".."))
                return new(false, "Use a path relative to the edited scene.");
            node = root.GetNodeOrNull(path);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            var matches = Descendants(root).Where(child => child.Name.ToString() == name).Take(2).ToArray();
            if (matches.Length > 1) return new(false, "Several nodes have that name. Supply a relative path.");
            node = matches.FirstOrDefault();
        }
        else return new(false, "Supply a relative path or node name.");
        if (node is null) return new(false, "Node was not found in the active scene.");

        var selection = Editor.GetSelection();
        selection.Clear();
        selection.AddNode(node);
        Editor.InspectObject(node);
        for (Node? target = node; target is not null && target != root; target = target.GetParent())
            if (target is Node3D spatial && preview.FocusNode(spatial))
                return new(true, $"Focused {node.Name}.");
        return new(false, $"Selected {node.Name}. Its location is unavailable in the active S³ preview.");
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var node in Descendants(child))
                yield return node;
    }

    private static Result PreviewUnavailable() => new(false, "Open a scene with an S³ space and let its preview render.");

    public void Close() => _closed = true;
}
#endif
