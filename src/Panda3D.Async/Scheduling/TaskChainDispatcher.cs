using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Panda3D.Core;

namespace Panda3D.Async.Scheduling;

/// <summary>
/// Routes managed callbacks onto a specific <see cref="AsyncTaskChain"/>.
///
/// Doubles as a <see cref="SynchronizationContext"/> so
/// <c>Task.Delay</c>, <c>HttpClient</c>, and any other code that
/// captures <c>SynchronizationContext.Current</c> resumes on the
/// originating chain after completion.
///
/// Work is drained by a single-shot <see cref="ManagedAsyncTask"/>
/// that is added to the chain on demand and returns <c>DS_done</c>
/// once the inbox is empty.  An idle chain holds no dispatcher tasks
/// and pays zero overhead.
/// </summary>
internal sealed class TaskChainDispatcher : SynchronizationContext, IManagedCallback {
    readonly IAsyncTaskChain _chain;
    readonly string _chainName;
    readonly ConcurrentQueue<Action> _inbox = new();
    GCHandle _selfHandle;
    int _drainScheduled;  // 0 or 1

    // Next-frame support: two lists swapped at the start of each Run().
    // PostNextFrame() always writes to _nextFramePending; Run() swaps it
    // into _nextFrameReady and drains that.  This guarantees callbacks
    // posted during the current epoch only fire in the NEXT epoch.
    readonly object _nextFrameLock = new();
    List<Action> _nextFramePending = new();
    List<Action> _nextFrameReady = new();

    /// <summary>Maximum actions drained per chain epoch.  Anything beyond yields DS_cont.</summary>
    const int DrainBudget = 256;

    public TaskChainDispatcher(IAsyncTaskChain chain, string chainName) {
        _chain = chain;
        _chainName = chainName;
        _selfHandle = GCHandle.Alloc(this);  // strong pin; freed only if dispatcher is disposed
    }

    public IAsyncTaskChain Chain => _chain;
    public string ChainName => _chainName;

    /// <summary>Posted actions are dispatched on the chain during its next epoch.</summary>
    public override void Post(SendOrPostCallback d, object? state) {
        Post(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state) {
        throw new NotSupportedException(
            "Blocking Send is not supported on a Panda3D TaskChainDispatcher. " +
            "Use Post or switch to the chain via await PandaTask.SwitchToChain.");
    }

    public void Post(Action action) {
        _inbox.Enqueue(action);
        ScheduleDrainIfNeeded();
    }

    /// <summary>
    /// Enqueue an action to run on the NEXT epoch, not the current one.
    /// Used by <c>PandaTask.NextFrame()</c> to guarantee a frame boundary.
    /// </summary>
    public void PostNextFrame(Action action) {
        lock (_nextFrameLock) {
            _nextFramePending.Add(action);
        }
        ScheduleDrainIfNeeded();
    }

    void ScheduleDrainIfNeeded() {
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;

        var dispatcherHandle = GCHandle.Alloc(this);
        var task = ManagedAsyncTask.Make(
            $"Panda3D.Async[{_chainName}]",
            ManagedTrampolines.RunFnPtr,
            ManagedTrampolines.FreeFnPtr,
            (ulong)(nint)GCHandle.ToIntPtr(dispatcherHandle));
        _chain.Add(task);
    }

    /// <summary>
    /// Drain callback — invoked by the chain during an epoch.
    /// Phase 1: fire next-frame callbacks (registered during a previous epoch).
    /// Phase 2: drain inbox (Spawn, Post, SynchronizationContext.Post).
    /// Returns DS_cont if there's pending next-frame work for the following
    /// epoch; DS_done otherwise.
    /// </summary>
    int IManagedCallback.Run() {
        // Phase 1: swap the pending next-frame list into ready and drain it.
        lock (_nextFrameLock) {
            (_nextFramePending, _nextFrameReady) = (_nextFrameReady, _nextFramePending);
        }

        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(this);
        try {
            for (int i = 0; i < _nextFrameReady.Count; i++) {
                try { _nextFrameReady[i](); }
                catch (Exception ex) {
                    PandaTaskScheduler.PublishUnobservedException(ex);
                }
            }
            _nextFrameReady.Clear();

            // Phase 2: drain inbox.
            var budget = DrainBudget;
            while (budget-- > 0 && _inbox.TryDequeue(out var a)) {
                try { a(); }
                catch (Exception ex) {
                    PandaTaskScheduler.PublishUnobservedException(ex);
                }
            }
        } finally {
            SynchronizationContext.SetSynchronizationContext(prev);
        }

        // Decide whether to come back next epoch.
        bool hasNextFrame;
        lock (_nextFrameLock) { hasNextFrame = _nextFramePending.Count > 0; }

        if (hasNextFrame || !_inbox.IsEmpty) {
            return (int)AsyncTask_DoneStatus.DS_cont;
        }

        Interlocked.Exchange(ref _drainScheduled, 0);

        // Race guard: work may have arrived between the check and clearing the flag.
        lock (_nextFrameLock) { hasNextFrame = _nextFramePending.Count > 0; }
        if (!_inbox.IsEmpty || hasNextFrame) {
            ScheduleDrainIfNeeded();
        }

        return (int)AsyncTask_DoneStatus.DS_done;
    }

    /// <summary>
    /// Called by the native task's free callback when *the drain task*
    /// is destroyed.  Does NOT dispose the dispatcher — the dispatcher
    /// outlives individual drain tasks.
    /// </summary>
    void IDisposable.Dispose() {
        // Intentional no-op.  The GCHandle held by the per-task user
        // data is freed by the trampoline; we keep _selfHandle alive
        // so future drain tasks can still resolve us.
    }
}
