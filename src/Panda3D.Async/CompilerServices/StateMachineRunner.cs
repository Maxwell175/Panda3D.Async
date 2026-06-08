using System;
using System.Runtime.CompilerServices;

namespace Panda3D.Async.CompilerServices;

/// <summary>
/// Non-generic facade on a pooled state-machine runner that the
/// method builder can talk to without needing the
/// <c>TStateMachine</c> type parameter at its field-storage site.
/// </summary>
internal interface IStateMachineRunnerPromise {
    PandaTask Task { get; }
    Action MoveNext { get; }
    void SetResult();
    void SetException(Exception ex);
}

internal interface IStateMachineRunnerPromise<T> {
    PandaTask<T> Task { get; }
    Action MoveNext { get; }
    void SetResult(T result);
    void SetException(Exception ex);
}

/// <summary>
/// Promise that hosts an async state machine without boxing it.
/// One pool per (state-machine-type) — i.e. one per async method.
/// Recycled into <see cref="TaskPool{T}"/> after <see cref="GetResult"/>.
/// </summary>
internal sealed class AsyncPandaTask<TStateMachine> :
    IPandaTaskSource,
    IStateMachineRunnerPromise,
    ITaskPoolNode<AsyncPandaTask<TStateMachine>>
    where TStateMachine : IAsyncStateMachine {

    TStateMachine _sm;
    readonly Action _moveNext;
    PandaTaskCompletionSourceCore<byte> _core;

    public AsyncPandaTask<TStateMachine>? NextNode { get; set; }

    AsyncPandaTask() {
        _sm = default!;
        _moveNext = Run;
    }

    /// <summary>
    /// Lazily create (or pull from pool) a runner for the given state
    /// machine and store it on the builder's slot.
    /// </summary>
    public static void SetStateMachine(ref TStateMachine sm,
                                        ref IStateMachineRunnerPromise? runner) {
        if (runner is not null) return;
        if (!TaskPool<AsyncPandaTask<TStateMachine>>.TryPop(out var r) || r is null) {
            r = new AsyncPandaTask<TStateMachine>();
        }
        r._core.Reset();
        // Order matters: write `runner = r` (which is `ref sm.<>t__builder._runner`)
        // before copying sm into r._sm.  The state machine contains the builder by
        // value; copying first freezes _runner=null inside r._sm.<>t__builder, and
        // the compiler-emitted <>t__builder.SetResult() at method completion
        // becomes a silent no-op.
        runner = r;
        r._sm = sm;
    }

    public PandaTask Task => new(this, _core.Version);
    public Action MoveNext => _moveNext;

    void Run() => _sm.MoveNext();

    public void SetResult() => _core.TrySetResult(default);
    public void SetException(Exception ex) => _core.TrySetException(ex);

    public PandaTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public PandaTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object?> continuation, object? state, short token)
        => _core.OnCompleted(continuation, state, token);

    public void GetResult(short token) {
        try {
            _core.GetResult(token);
        }
        finally {
            _sm = default!;
            TaskPool<AsyncPandaTask<TStateMachine>>.TryPush(this);
        }
    }
}

/// <summary>
/// Typed runner — used for <c>async PandaTask&lt;T&gt;</c> methods.
/// </summary>
internal sealed class AsyncPandaTask<TStateMachine, T> :
    IPandaTaskSource<T>,
    IStateMachineRunnerPromise<T>,
    ITaskPoolNode<AsyncPandaTask<TStateMachine, T>>
    where TStateMachine : IAsyncStateMachine {

    TStateMachine _sm;
    readonly Action _moveNext;
    PandaTaskCompletionSourceCore<T> _core;

    public AsyncPandaTask<TStateMachine, T>? NextNode { get; set; }

    AsyncPandaTask() {
        _sm = default!;
        _moveNext = Run;
    }

    public static void SetStateMachine(ref TStateMachine sm,
                                        ref IStateMachineRunnerPromise<T>? runner) {
        if (runner is not null) return;
        if (!TaskPool<AsyncPandaTask<TStateMachine, T>>.TryPop(out var r) || r is null) {
            r = new AsyncPandaTask<TStateMachine, T>();
        }
        r._core.Reset();
        // See AsyncPandaTask<TStateMachine>.SetStateMachine for why the order matters.
        runner = r;
        r._sm = sm;
    }

    public PandaTask<T> Task => new(this, _core.Version);
    public Action MoveNext => _moveNext;

    void Run() => _sm.MoveNext();

    public void SetResult(T result) => _core.TrySetResult(result);
    public void SetException(Exception ex) => _core.TrySetException(ex);

    public PandaTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public PandaTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object?> continuation, object? state, short token)
        => _core.OnCompleted(continuation, state, token);

    void IPandaTaskSource.GetResult(short token) => GetResult(token);

    public T GetResult(short token) {
        try {
            return _core.GetResult(token);
        }
        finally {
            _sm = default!;
            TaskPool<AsyncPandaTask<TStateMachine, T>>.TryPush(this);
        }
    }
}
