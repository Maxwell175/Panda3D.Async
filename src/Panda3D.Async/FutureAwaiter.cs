using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Panda3D.Async.Scheduling;
using Panda3D.Core;

namespace Panda3D.Async;

/// <summary>
/// Awaiter that lets C# <c>await</c> a native <see cref="IAsyncFuture"/>.
/// Already-done futures resume the continuation synchronously; otherwise we
/// register a <see cref="FutureWaiterCallback"/> with the future and post the
/// continuation back to the current <see cref="TaskChainDispatcher"/> when it fires.
/// </summary>
public readonly struct FutureAwaiter : ICriticalNotifyCompletion {
    readonly IAsyncFuture _future;

    public FutureAwaiter(IAsyncFuture future) {
        _future = future ?? throw new ArgumentNullException(nameof(future));
    }

    public bool IsCompleted => _future.Done();

    public void GetResult() {
        if (_future.Cancelled()) {
            throw new TaskCanceledException("AsyncFuture was cancelled.");
        }
    }

    public void OnCompleted(Action continuation) => Register(continuation);
    public void UnsafeOnCompleted(Action continuation) => Register(continuation);

    void Register(Action continuation) {
        if (_future.Done()) {
            continuation();
            return;
        }

        // Null dispatcher means we're not running inside a chain (e.g. console
        // tests); resume directly on whatever thread completes the future.
        var dispatcher = DispatcherTable.GetCurrent();
        var waiter = FutureWaiterCallback.Rent(continuation, dispatcher);

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(waiter);
        var task = ManagedAsyncTask.Make(
            "Panda3D.Async[waiter]",
            ManagedTrampolines.RunFnPtr,
            ManagedTrampolines.FreeFnPtr,
            (ulong)(nint)System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));

        if (!_future.AddWaitingTaskCsharp(task)) {
            // Raced the future's own completion — dispatch inline.
            waiter.RunInline();
        }
    }
}

/// <summary>
/// Bridges a <c>Panda3D.Core.AsyncFuture</c> waiter activation to a
/// managed <see cref="Action"/>.  Pooled — allocated once per
/// concurrent waiter in flight; recycled on completion.
/// </summary>
internal sealed class FutureWaiterCallback :
    IManagedCallback,
    CompilerServices.ITaskPoolNode<FutureWaiterCallback> {

    Action? _continuation;
    TaskChainDispatcher? _dispatcher;
    int _fired;

    public FutureWaiterCallback? NextNode { get; set; }

    FutureWaiterCallback() {}

    public static FutureWaiterCallback Rent(Action continuation, TaskChainDispatcher? dispatcher) {
        if (!CompilerServices.TaskPool<FutureWaiterCallback>.TryPop(out var w) || w is null) {
            w = new FutureWaiterCallback();
        }
        w._continuation = continuation;
        w._dispatcher = dispatcher;
        w._fired = 0;
        return w;
    }

    /// <summary>Invoked when the registrar races native completion and discovers the future is already done.</summary>
    public void RunInline() {
        if (System.Threading.Interlocked.Exchange(ref _fired, 1) != 0) return;
        Dispatch();
    }

    int IManagedCallback.Run() {
        if (System.Threading.Interlocked.Exchange(ref _fired, 1) == 0) {
            Dispatch();
        }
        return (int)AsyncTaskDoneStatus.DsDone;
    }

    void Dispatch() {
        var c = _continuation;
        var d = _dispatcher;
        _continuation = null;
        _dispatcher = null;

        if (c is null) return;
        if (d is not null) {
            d.Post(c);
        } else {
            try { c(); }
            catch (Exception ex) {
                PandaTaskScheduler.PublishUnobservedException(ex);
            }
        }
    }

    void IDisposable.Dispose() {
        // Fires when the wrapping ManagedAsyncTask is freed — return to pool.
        _continuation = null;
        _dispatcher = null;
        CompilerServices.TaskPool<FutureWaiterCallback>.TryPush(this);
    }
}
