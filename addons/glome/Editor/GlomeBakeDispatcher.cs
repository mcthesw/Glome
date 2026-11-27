#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Glome.Editor;

// Keep a preview bake's main-thread continuations with the editor session. Godot's
// process-wide synchronization context otherwise roots an old assembly while the
// engine is blocked waiting for its collectible context to unload.
internal sealed class GlomeBakeDispatcher : SynchronizationContext, IDisposable
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = [];
    private bool _closed;
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_posts)
            if (!_closed) _posts.Enqueue((callback, state));
    }

    internal Task<GlomeEditorView> Start(IGlomeEditorViewProvider provider, S3Space3D space, CancellationToken token)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try { return provider.BakeAsync(space, token); }
        finally { SetSynchronizationContext(previous); }
    }

    internal void Pump()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            int pending;
            lock (_posts) pending = _posts.Count;
            for (int i = 0; i < pending; i++)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (_posts) { if (!_posts.TryDequeue(out work)) return; }
                work.Callback(work.State);
            }
        }
        finally { SetSynchronizationContext(previous); }
    }

    public void Dispose()
    {
        lock (_posts)
        {
            _closed = true;
            _posts.Clear();
        }
    }
}
#endif
