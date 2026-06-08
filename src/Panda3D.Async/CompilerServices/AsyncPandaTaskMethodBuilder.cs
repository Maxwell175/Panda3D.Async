using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Panda3D.Async.CompilerServices;

/// <summary>
/// Custom builder for <c>async PandaTask</c> methods.  Parallels
/// UniTask's <c>AsyncUniTaskMethodBuilder</c>: on the first
/// suspension, <see cref="AwaitUnsafeOnCompleted"/> promotes the
/// struct state machine into a pooled runner promise so subsequent
/// suspensions don't allocate.
/// </summary>
public struct AsyncPandaTaskMethodBuilder {
    // The runner is null until the first suspension.  A
    // sync-completed async method never allocates one.
    IStateMachineRunnerPromise? _runner;
    IPandaTaskSource? _faultedSource;

    public static AsyncPandaTaskMethodBuilder Create() => default;

    public PandaTask Task {
        get {
            if (_runner is not null) return _runner.Task;
            if (_faultedSource is not null) return new PandaTask(_faultedSource, 1);
            return PandaTask.CompletedTask;
        }
    }

    [DebuggerHidden]
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine {
        stateMachine.MoveNext();
    }

    [DebuggerHidden]
    public void SetStateMachine(IAsyncStateMachine stateMachine) {
        // UniTask-style builder doesn't use this — the state machine is captured
        // by value via AsyncPandaTask<TSM>.SetStateMachine.
    }

    [DebuggerHidden]
    public void SetResult() {
        // _runner==null means we sync-completed; Task returns CompletedTask.
        if (_runner is not null) _runner.SetResult();
    }

    [DebuggerHidden]
    public void SetException(Exception exception) {
        if (_runner is not null) {
            _runner.SetException(exception);
        } else {
            _faultedSource = FaultedSource.Get(exception);
        }
    }

    [DebuggerHidden]
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine {
        AsyncPandaTask<TStateMachine>.SetStateMachine(ref stateMachine, ref _runner);
        awaiter.OnCompleted(_runner!.MoveNext);
    }

    [DebuggerHidden]
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine {
        AsyncPandaTask<TStateMachine>.SetStateMachine(ref stateMachine, ref _runner);
        awaiter.UnsafeOnCompleted(_runner!.MoveNext);
    }
}

/// <summary>
/// Generic-result variant — <c>async PandaTask&lt;T&gt;</c>.
/// </summary>
public struct AsyncPandaTaskMethodBuilder<T> {
    IStateMachineRunnerPromise<T>? _runner;
    IPandaTaskSource<T>? _faultedSource;
    T _syncResult;
    bool _hasSyncResult;

    public static AsyncPandaTaskMethodBuilder<T> Create() => default;

    public PandaTask<T> Task {
        get {
            if (_runner is not null) return _runner.Task;
            if (_faultedSource is not null) return new PandaTask<T>(_faultedSource, 1);
            return _hasSyncResult ? new PandaTask<T>(_syncResult) : default;
        }
    }

    [DebuggerHidden]
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine {
        stateMachine.MoveNext();
    }

    [DebuggerHidden]
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    [DebuggerHidden]
    public void SetResult(T result) {
        if (_runner is not null) {
            _runner.SetResult(result);
        } else {
            _syncResult = result;
            _hasSyncResult = true;
        }
    }

    [DebuggerHidden]
    public void SetException(Exception exception) {
        if (_runner is not null) {
            _runner.SetException(exception);
        } else {
            _faultedSource = FaultedSource<T>.Get(exception);
        }
    }

    [DebuggerHidden]
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine {
        AsyncPandaTask<TStateMachine, T>.SetStateMachine(ref stateMachine, ref _runner);
        awaiter.OnCompleted(_runner!.MoveNext);
    }

    [DebuggerHidden]
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine {
        AsyncPandaTask<TStateMachine, T>.SetStateMachine(ref stateMachine, ref _runner);
        awaiter.UnsafeOnCompleted(_runner!.MoveNext);
    }
}

/// <summary>
/// Throwaway promise used to surface a synchronously-thrown
/// exception from an <c>async PandaTask</c> method.  Not pooled — we
/// assume sync exceptions are rare.
/// </summary>
internal sealed class FaultedSource : IPandaTaskSource {
    readonly Exception _ex;
    FaultedSource(Exception ex) { _ex = ex; }
    public static FaultedSource Get(Exception ex) => new(ex);

    public PandaTaskStatus GetStatus(short token)
        => _ex is OperationCanceledException ? PandaTaskStatus.Canceled : PandaTaskStatus.Faulted;
    public PandaTaskStatus UnsafeGetStatus() => GetStatus(0);

    public void OnCompleted(Action<object?> continuation, object? state, short token)
        => continuation(state);

    public void GetResult(short token)
        => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_ex);
}

internal sealed class FaultedSource<T> : IPandaTaskSource<T> {
    readonly Exception _ex;
    FaultedSource(Exception ex) { _ex = ex; }
    public static FaultedSource<T> Get(Exception ex) => new(ex);

    public PandaTaskStatus GetStatus(short token)
        => _ex is OperationCanceledException ? PandaTaskStatus.Canceled : PandaTaskStatus.Faulted;
    public PandaTaskStatus UnsafeGetStatus() => GetStatus(0);

    public void OnCompleted(Action<object?> continuation, object? state, short token)
        => continuation(state);

    void IPandaTaskSource.GetResult(short token)
        => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_ex);

    public T GetResult(short token) {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_ex);
        return default!;
    }
}
