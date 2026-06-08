using System;
using System.Threading;

namespace Panda3D.Async.Scheduling;

/// <summary>
/// Helpers that cooperate with <see cref="TaskChainDispatcher"/> to
/// control whether continuations capture the current sync context.
/// </summary>
internal static class PandaSynchronizationContext {
    /// <summary>
    /// Temporarily replaces <c>SynchronizationContext.Current</c>
    /// with <c>null</c> for the lifetime of the returned disposable.
    /// Used by <c>ConfigureAwait(false)</c> to let a <c>Task</c>
    /// awaiter's <c>OnCompleted</c> skip posting to the chain
    /// dispatcher.
    /// </summary>
    public static SuppressScope SuppressCapture() => new(SynchronizationContext.Current);

    public readonly struct SuppressScope : IDisposable {
        readonly SynchronizationContext? _prev;
        public SuppressScope(SynchronizationContext? prev) {
            _prev = prev;
            SynchronizationContext.SetSynchronizationContext(null);
        }
        public void Dispose() {
            SynchronizationContext.SetSynchronizationContext(_prev);
        }
    }
}
