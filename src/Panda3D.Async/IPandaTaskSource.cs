using System;

namespace Panda3D.Async;

/// <summary>
/// Lifecycle of an <see cref="IPandaTaskSource"/>.  Mirrors UniTask's
/// UniTaskStatus so the state-machine builder can reason about completion
/// without forcing the source to rethrow an exception.
/// </summary>
public enum PandaTaskStatus : byte {
    /// <summary>Completion has not yet been signalled.</summary>
    Pending = 0,

    /// <summary>Completed with a result (no exception).</summary>
    Succeeded = 1,

    /// <summary>Completed with <see cref="OperationCanceledException"/>-equivalent state.</summary>
    Canceled = 2,

    /// <summary>Completed with an exception other than cancellation.</summary>
    Faulted = 3,
}

public static class PandaTaskStatusExtensions {
    public static bool IsCompleted(this PandaTaskStatus s) => s != PandaTaskStatus.Pending;
    public static bool IsCompletedSuccessfully(this PandaTaskStatus s) => s == PandaTaskStatus.Succeeded;
    public static bool IsCanceled(this PandaTaskStatus s) => s == PandaTaskStatus.Canceled;
    public static bool IsFaulted(this PandaTaskStatus s) => s == PandaTaskStatus.Faulted;
}

/// <summary>
/// Backing source for a <see cref="PandaTask"/>.  A pair of
/// (<see cref="IPandaTaskSource"/>, <see cref="short"/> token) uniquely
/// identifies a promise.  The token is bumped on <c>Reset()</c> so a
/// pooled source can invalidate stale awaiters.
/// </summary>
public interface IPandaTaskSource {
    /// <summary>
    /// Current status.  Throws <see cref="InvalidOperationException"/>
    /// if <paramref name="token"/> doesn't match this source's version.
    /// </summary>
    PandaTaskStatus GetStatus(short token);

    /// <summary>
    /// Register <paramref name="continuation"/> to run when the source
    /// completes.  Called by <see cref="PandaTask.Awaiter.OnCompleted"/>.
    /// If already completed, the continuation MAY be invoked
    /// synchronously from this call.
    /// </summary>
    void OnCompleted(Action<object?> continuation, object? state, short token);

    /// <summary>
    /// Consume the result (or rethrow the stored exception).  Must only
    /// be called after <see cref="GetStatus"/> indicates completion.
    /// </summary>
    void GetResult(short token);

    /// <summary>
    /// Fast-path status read that does not validate <c>token</c>.  Used
    /// by debug/tracing code.
    /// </summary>
    PandaTaskStatus UnsafeGetStatus();
}

/// <summary>
/// Typed source variant — <see cref="GetResult"/> returns a value of
/// type <typeparamref name="T"/>.
/// </summary>
public interface IPandaTaskSource<out T> : IPandaTaskSource {
    new T GetResult(short token);
}
