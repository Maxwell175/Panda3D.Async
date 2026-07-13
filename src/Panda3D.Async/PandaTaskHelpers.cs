using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Panda3D.Async.CompilerServices;
using Panda3D.Async.Scheduling;
using Panda3D.Core;

namespace Panda3D.Async;

public readonly partial struct PandaTask {
    /// <summary>
    /// Fire-and-forget a coroutine onto a named chain.  The
    /// coroutine's first <c>await</c> hop puts it on the chain;
    /// until then it runs synchronously on the calling thread.
    /// </summary>
    public static void Spawn(Func<PandaTask> body, string chainName = "default") {
        if (body is null) throw new ArgumentNullException(nameof(body));

        // Post via the dispatcher so the body runs on the target chain regardless
        // of which thread called Spawn.
        var dispatcher = DispatcherTable.GetOrCreate(chainName);
        dispatcher.Post(() => {
            try {
                var task = body();
                task.Forget();
            }
            catch (Exception ex) {
                PandaTaskScheduler.PublishUnobservedException(ex);
            }
        });
    }

    /// <summary>Resume on the next epoch of the current chain.</summary>
    public static PandaTask NextFrame() {
        var source = FrameYieldSource.Rent(DispatcherTable.GetCurrent());
        return new PandaTask(source, source.Version);
    }

    /// <summary>Resume after at least <paramref name="seconds"/> on the current chain.</summary>
    public static PandaTask Delay(double seconds) {
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var source = DelaySource.Rent(DispatcherTable.GetCurrent(), seconds);
        return new PandaTask(source, source.Version);
    }

    /// <summary>Cooperative yield within the current chain.</summary>
    public static PandaTask Yield() => NextFrame();

    /// <summary>
    /// Re-enter the named chain.  After <c>await
    /// PandaTask.SwitchToChain(name)</c>, code runs on that chain
    /// until the next explicit switch.
    /// </summary>
    public static PandaTask SwitchToChain(string chainName) {
        if (chainName is null) throw new ArgumentNullException(nameof(chainName));
        var source = SwitchChainSource.Rent(chainName);
        return new PandaTask(source, source.Version);
    }

    /// <summary>
    /// Await all of the given futures.  Completes when every future
    /// has finished (with or without cancellation).  Cancelling the
    /// outer PandaTask cancels every pending inner future.
    /// </summary>
    /// <remarks>
    /// This is <c>AsyncFuture::gather()</c> -- the engine's own "all of these"
    /// future, which wires itself up as a waiter on each child and, when cancelled,
    /// cancels the children still pending.  It used to be reimplemented here (a
    /// GCHandle and a ManagedAsyncTask per future, plus a hand-rolled pending
    /// counter with its own completion race) because <c>gather_csharp</c> took a
    /// <c>T **, int</c> array, which interrogate has no marshalling for -- so the
    /// binding was silently dropped and nobody noticed it was missing.  It now
    /// takes a container, and this is one waiter on one future.
    /// </remarks>
    public static PandaTask WhenAll(params IAsyncFuture[] futures) {
        if (futures is null) throw new ArgumentNullException(nameof(futures));

        var gathered = new Futures();
        foreach (var future in futures) {
            if (future is not null) {
                gathered.Add(future);
            }
        }
        if (gathered.Count == 0) {
            return CompletedTask;
        }

        var source = WhenAllFuturesSource.Rent(AsyncFuture.GatherCsharp(gathered));
        return new PandaTask(source, source.Version);
    }

    /// <summary>Await every task in <paramref name="tasks"/>.</summary>
    public static PandaTask WhenAll(params PandaTask[] tasks) {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        return WhenAllImpl(tasks);
    }

    static async PandaTask WhenAllImpl(PandaTask[] tasks) {
        foreach (var t in tasks) {
            await t;
        }
    }

    /// <summary>Wrap a <see cref="System.Threading.Tasks.Task"/> as a <see cref="PandaTask"/>.</summary>
    public static PandaTask FromTask(Task task) {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (task.IsCompletedSuccessfully) return CompletedTask;

        var tcs = new PandaTaskCompletionSource();
        task.ContinueWith(static (t, state) => {
            var s = (PandaTaskCompletionSource)state!;
            if (t.IsCanceled) s.TrySetCanceled();
            else if (t.IsFaulted) s.TrySetException(t.Exception!.GetBaseException());
            else s.TrySetResult();
        }, tcs, TaskContinuationOptions.ExecuteSynchronously);
        return tcs.Task;
    }

    /// <summary>Wrap a <see cref="System.Threading.Tasks.Task{T}"/> as a <see cref="PandaTask{T}"/>.</summary>
    public static PandaTask<T> FromTask<T>(Task<T> task) {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (task.IsCompletedSuccessfully) return new PandaTask<T>(task.Result);

        var tcs = new PandaTaskCompletionSource<T>();
        task.ContinueWith(static (t, state) => {
            var s = (PandaTaskCompletionSource<T>)state!;
            if (t.IsCanceled) s.TrySetCanceled();
            else if (t.IsFaulted) s.TrySetException(t.Exception!.GetBaseException());
            else s.TrySetResult(t.Result);
        }, tcs, TaskContinuationOptions.ExecuteSynchronously);
        return tcs.Task;
    }
}

// ---- pooled sources backing NextFrame / Delay / SwitchToChain / WhenAll ----

internal sealed class FrameYieldSource :
    IPandaTaskSource,
    ITaskPoolNode<FrameYieldSource> {

    PandaTaskCompletionSourceCore<byte> _core;
    public FrameYieldSource? NextNode { get; set; }

    FrameYieldSource() {}

    public static FrameYieldSource Rent(TaskChainDispatcher? dispatcher) {
        if (!TaskPool<FrameYieldSource>.TryPop(out var s) || s is null) {
            s = new FrameYieldSource();
        }
        s._core.Reset();

        if (dispatcher is null) {
            s._core.TrySetResult(default);
        } else {
            dispatcher.PostNextFrame(s.Complete);
        }
        return s;
    }

    void Complete() {
        _core.TrySetResult(default);
    }

    public short Version => _core.Version;

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();
    void IPandaTaskSource.OnCompleted(Action<object?> c, object? s, short token)
        => _core.OnCompleted(c, s, token);
    void IPandaTaskSource.GetResult(short token) {
        _core.GetResult(token);
        TaskPool<FrameYieldSource>.TryPush(this);
    }
}

internal sealed class DelaySource :
    IPandaTaskSource,
    IManagedCallback,
    ITaskPoolNode<DelaySource> {

    PandaTaskCompletionSourceCore<byte> _core;
    public DelaySource? NextNode { get; set; }

    DelaySource() {}

    public static DelaySource Rent(TaskChainDispatcher? dispatcher, double seconds) {
        if (!TaskPool<DelaySource>.TryPop(out var s) || s is null) {
            s = new DelaySource();
        }
        s._core.Reset();

        if (dispatcher is null) {
            s._core.TrySetResult(default);
        } else {
            var handle = GCHandle.Alloc(s);
            var task = ManagedAsyncTask.Make(
                "Panda3D.Async.Delay",
                ManagedTrampolines.RunFnPtr,
                ManagedTrampolines.FreeFnPtr,
                (ulong)(nint)GCHandle.ToIntPtr(handle));
            task.SetDelay(seconds);
            dispatcher.Chain.Add(task);
        }
        return s;
    }

    public short Version => _core.Version;

    int IManagedCallback.Run() {
        _core.TrySetResult(default);
        return (int)AsyncTaskDoneStatus.DsDone;
    }

    void IDisposable.Dispose() => TaskPool<DelaySource>.TryPush(this);

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();
    void IPandaTaskSource.OnCompleted(Action<object?> c, object? s, short token)
        => _core.OnCompleted(c, s, token);
    void IPandaTaskSource.GetResult(short token) => _core.GetResult(token);
}

/// <summary>
/// Resumes on a specific chain.  On OnCompleted we post the
/// continuation to the target chain's dispatcher.
/// </summary>
internal sealed class SwitchChainSource :
    IPandaTaskSource,
    ITaskPoolNode<SwitchChainSource> {

    string _chainName = "";
    PandaTaskCompletionSourceCore<byte> _core;
    public SwitchChainSource? NextNode { get; set; }

    SwitchChainSource() {}

    public static SwitchChainSource Rent(string chainName) {
        if (!TaskPool<SwitchChainSource>.TryPop(out var s) || s is null) {
            s = new SwitchChainSource();
        }
        s._core.Reset();
        s._chainName = chainName;
        return s;
    }

    public short Version => _core.Version;

    // OnCompleted reposts to the target chain's dispatcher; that posted callback is
    // what flips status to Succeeded and invokes the awaiter's continuation, so the
    // awaiter always suspends once and resumes on the target chain.

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();

    void IPandaTaskSource.OnCompleted(Action<object?> continuation, object? state, short token) {
        var dispatcher = DispatcherTable.GetOrCreate(_chainName);
        dispatcher.Post(() => {
            _core.TrySetResult(default);
            continuation(state);
        });
    }

    void IPandaTaskSource.GetResult(short token) {
        _core.GetResult(token);
        TaskPool<SwitchChainSource>.TryPush(this);
    }
}

/// <summary>
/// Waits for a single <see cref="IAsyncFuture"/> -- the gathering future returned by
/// <c>AsyncFuture.GatherCsharp</c> -- to complete, via one
/// <c>AddWaitingTaskCsharp</c> waiter.
/// </summary>
internal sealed class WhenAllFuturesSource :
    IPandaTaskSource,
    IManagedCallback,
    ITaskPoolNode<WhenAllFuturesSource> {

    PandaTaskCompletionSourceCore<byte> _core;
    IAsyncFuture? _future;
    public WhenAllFuturesSource? NextNode { get; set; }

    WhenAllFuturesSource() {}

    public static WhenAllFuturesSource Rent(IAsyncFuture gathered) {
        if (!TaskPool<WhenAllFuturesSource>.TryPop(out var s) || s is null) {
            s = new WhenAllFuturesSource();
        }
        s._core.Reset();
        s._future = gathered;

        if (gathered.Done()) {
            s._core.TrySetResult(default);
            return s;
        }

        var handle = GCHandle.Alloc(s);
        var task = ManagedAsyncTask.Make(
            "Panda3D.Async.WhenAll",
            ManagedTrampolines.RunFnPtr,
            ManagedTrampolines.FreeFnPtr,
            (ulong)(nint)GCHandle.ToIntPtr(handle));
        if (!gathered.AddWaitingTaskCsharp(task)) {
            // Raced with the gathering future completing.
            s._core.TrySetResult(default);
        }
        return s;
    }

    public short Version => _core.Version;

    int IManagedCallback.Run() {
        _core.TrySetResult(default);
        return (int)AsyncTaskDoneStatus.DsDone;
    }

    void IDisposable.Dispose() {
        // Not pooled — one per WhenAll call, off the hot path.
    }

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();
    void IPandaTaskSource.OnCompleted(Action<object?> c, object? s, short token)
        => _core.OnCompleted(c, s, token);
    void IPandaTaskSource.GetResult(short token) => _core.GetResult(token);
}
