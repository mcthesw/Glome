#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glome.Core;
using Godot;

namespace Glome.Editor;

// A view owns the exact CPU pick snapshot and matching GPU buffer layout.
// PrepareFrame can upload extension data and select a specialization before each draw.
public sealed record GlomeEditorView(S3BakeSnapshot Snapshot, GlomeBuffers Buffers, S3Frame InitialView,
    S3RenderProgram? Program = null, Func<GlomeComputeRenderer, S3Frame, float, int>? PrepareFrame = null)
{
    public IReadOnlyDictionary<ulong, (LightGroup Group, int Slot)>? NativeLightSlots { get; init; }
}

public interface IGlomeEditorViewProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool AppliesTo(S3Space3D space);
    Vector2I Resolution(Vector2I panelPixels) => panelPixels;
    CanvasItem.TextureFilterEnum DisplayFilter => CanvasItem.TextureFilterEnum.Linear;
    bool Animated => false;
    bool TryApplyChange(S3Space3D space, GlomeEditorView view, GlomeViewChange change, GlomeComputeRenderer renderer) => false;
    GlomeEditorView Bake(S3Space3D space);
    Task<GlomeEditorView> BakeAsync(S3Space3D space, CancellationToken cancellation) =>
        Task.FromResult(Bake(space)); // Existing synchronous extension providers remain supported.
}

public static class GlomeEditorViews
{
    internal sealed class Neutral(S3ShadingMode mode) : IGlomeEditorViewProvider
    {
        public string Id => mode.ToString();
        public string DisplayName => Id;
        public bool AppliesTo(S3Space3D space) => true;
        private GlomeBakeOptions Options(S3Space3D space) => new(Visibility: space.BakeVisibilityPolicy,
            IncludeOwners: true, CaptureAuthoringSources: true) { Timings = new(DisplayName) };

        public GlomeEditorView Bake(S3Space3D space)
        {
            var options = Options(space);
            return Build(space, GlomeBaker.Bake(space, options), options.Timings!);
        }

        public async Task<GlomeEditorView> BakeAsync(S3Space3D space, CancellationToken cancellation)
        {
            var options = Options(space);
            var snapshot = await GlomeBaker.BakeAsync(space, options, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return Build(space, snapshot, options.Timings!);
        }

        internal void ApplyEnvironment(S3Space3D space, GlomeEditorView view, GlomeComputeRenderer renderer) =>
            renderer.UpdateEnvironment(Environment(space, view.Snapshot));

        private EnvironmentBlock Environment(S3Space3D space, S3BakeSnapshot snapshot)
        {
            var env = S3Environment.CaptureOrDefault(space.Environment, snapshot.Environment.ModeGroundMaterial.Y);
            env.ModeGroundMaterial.X = (int)mode;
            return env;
        }

        public bool TryApplyChange(S3Space3D space, GlomeEditorView view, GlomeViewChange change, GlomeComputeRenderer renderer)
        {
            if (change.Revision != view.Snapshot.Revision || space.LastBake != view.Snapshot) return false;
            if (change.TryLight(space, view, out int slot, out var record)) renderer.UpdateLight(slot, record);
            else if (change.IsEnvironment(space, view.Snapshot)) ApplyEnvironment(space, view, renderer);
            else return false;
            return true;
        }

        private GlomeEditorView Build(S3Space3D space, S3BakeSnapshot snapshot, GlomeBakeTimings timings)
        {
            long start = Stopwatch.GetTimestamp();
            var table = new GlomeLightTable<ulong>(snapshot.Lights.Select((light, i) => (snapshot.LightSources[i].Id, light)));
            var buffers = GlomeBuffers.Pack(snapshot, table.Pack()) with { Environment = Environment(space, snapshot), Timings = timings };
            var slots = snapshot.Lights.Select((light, i) => (light.Group, snapshot.LightSources[i].Id))
                .ToDictionary(source => source.Id, source => (source.Group, table[source.Id]));
            timings.Pack = GlomeBakeTimings.Since(start);
            return new(snapshot, buffers, space.InitialView()) { NativeLightSlots = slots };
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
