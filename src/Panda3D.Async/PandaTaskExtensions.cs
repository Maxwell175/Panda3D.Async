using System;
using System.Threading.Tasks;
using Panda3D.Core;

namespace Panda3D.Async;

/// <summary>
/// Interop extension methods: <see cref="PandaTask"/> <-&gt;
/// <see cref="System.Threading.Tasks.Task"/>, plus the
/// <c>GetAwaiter</c> extension that lets callers write <c>await
/// future</c> without needing a partial-class helper inside
/// <c>Panda3D.Interop</c>.
/// </summary>
public static class PandaTaskExtensions {
    // ---- IAsyncFuture awaiter (fallback for consumers who only pull
    // in Panda3D.Async; the Panda3D.Interop partial class produces
    // the same awaiter via an instance method).

    /// <summary>
    /// Enables <c>await future</c> when the generated
    /// <c>Panda3D.Core.AsyncFuture</c> does not already provide an
    /// instance <c>GetAwaiter()</c> method.  Safe to have both —
    /// instance methods win over extension methods in C# lookup.
    /// </summary>
    public static FutureAwaiter GetAwaiter(this IAsyncFuture future)
        => new(future);

    // ---- PandaTask <-> Task bridge

    /// <summary>Expose a <see cref="PandaTask"/> as a <see cref="Task"/>.</summary>
    public static Task AsTask(this PandaTask task) {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AwaitAndBridge(task, tcs);
        return tcs.Task;

        static async void AwaitAndBridge(PandaTask t, TaskCompletionSource s) {
            try {
                await t;
                s.TrySetResult();
            } catch (OperationCanceledException) {
                s.TrySetCanceled();
            } catch (Exception ex) {
                s.TrySetException(ex);
            }
        }
    }

    /// <summary>Expose a <see cref="PandaTask{T}"/> as a <see cref="Task{T}"/>.</summary>
    public static Task<T> AsTask<T>(this PandaTask<T> task) {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        AwaitAndBridge(task, tcs);
        return tcs.Task;

        static async void AwaitAndBridge(PandaTask<T> t, TaskCompletionSource<T> s) {
            try {
                var r = await t;
                s.TrySetResult(r);
            } catch (OperationCanceledException) {
                s.TrySetCanceled();
            } catch (Exception ex) {
                s.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Wrap a <see cref="Task"/> as a <see cref="PandaTask"/>.  Users
    /// typically don't need this — <c>await</c>ing a <see cref="Task"/>
    /// directly from inside a PandaTask coroutine already routes the
    /// continuation back onto the chain via the captured
    /// <see cref="System.Threading.SynchronizationContext"/>.  This
    /// overload is useful only when you need to store the result as
    /// a <see cref="PandaTask"/> field.
    /// </summary>
    public static PandaTask ToPandaTask(this Task task) => PandaTask.FromTask(task);

    /// <summary>Wrap a <see cref="Task{T}"/> as a <see cref="PandaTask{T}"/>.</summary>
    public static PandaTask<T> ToPandaTask<T>(this Task<T> task) => PandaTask.FromTask(task);
}
