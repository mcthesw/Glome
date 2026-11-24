#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Glome.Core;
using Godot;

namespace Glome.Editor;

// A view owns the exact CPU pick snapshot and matching GPU buffer layout.
// PrepareFrame can upload extension data and select a specialization before each draw.
public sealed record GlomeEditorView(S3BakeSnapshot Snapshot, GlomeBuffers Buffers, S3Frame InitialView,
    S3RenderProgram? Program = null, Func<GlomeComputeRenderer, S3Frame, float, int>? PrepareFrame = null);

public interface IGlomeEditorViewProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool AppliesTo(S3Space3D space);
    Vector2I Resolution(Vector2I panelPixels) => panelPixels;
    CanvasItem.TextureFilterEnum DisplayFilter => CanvasItem.TextureFilterEnum.Linear;
    bool Animated => false;
    GlomeEditorView Bake(S3Space3D space);
}

public static class GlomeEditorViews
{
    private sealed class Neutral(S3ShadingMode mode) : IGlomeEditorViewProvider
    {
        public string Id => mode.ToString();
        public string DisplayName => Id;
        public bool AppliesTo(S3Space3D space) => true;
        public GlomeEditorView Bake(S3Space3D space)
        {
            var snapshot = GlomeBaker.Bake(space, visibility: space.BakeVisibilityPolicy, includeOwners: true);
            var env = snapshot.Environment;
            env.ModeGroundMaterial.X = (int)mode;
            var buffers = GlomeBuffers.Pack(snapshot) with { Environment = env };
            return new(snapshot, buffers, space.InitialView());
        }
    }

    private static readonly List<IGlomeEditorViewProvider> Providers =
        [new Neutral(S3ShadingMode.StandardLit), new Neutral(S3ShadingMode.ClayClean), new Neutral(S3ShadingMode.Unlit)];
    internal static event Action? Changed;
    internal static IGlomeEditorViewProvider[] For(S3Space3D space) => Providers.Where(p => p.AppliesTo(space)).ToArray();

    public static IDisposable Register(IGlomeEditorViewProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id) || Providers.Any(p => p.Id == provider.Id))
            throw new ArgumentException($"Editor view ID '{provider.Id}' is empty or already registered.", nameof(provider));
        Providers.Add(provider);
        Changed?.Invoke();
        return new Registration(provider);
    }

    private sealed class Registration(IGlomeEditorViewProvider provider) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Providers.RemoveAll(p => ReferenceEquals(p, provider));
            Changed?.Invoke();
        }
    }
}
#endif
