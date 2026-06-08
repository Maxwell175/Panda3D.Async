using System;
using System.Threading;
using Panda3D.Async.CompilerServices;
using Panda3D.Async.Scheduling;

namespace Panda3D.Async;

/// <summary>
/// <c>TaskCompletionSource</c> analogue for <see cref="PandaTask"/>.  Safe to complete
/// from any thread (CAS on the pending→done transition); the awaiter's chain dispatcher
/// is captured on first await so completions from off-chain threads resume on-chain.
/// </summary>
public sealed class PandaTaskCompletionSource : IPandaTaskSource {
    PandaTaskCompletionSourceCore<byte> _core;
    TaskChainDispatcher? _capturedDispatcher;

    public PandaTaskCompletionSource() {
        _core.Reset();
    }

    public PandaTask Task => new(this, _core.Version);

    public bool TrySetResult() {
        var ok = _core.TrySetResult(default);
        return ok;
    }

    public bool TrySetException(Exception exception) {
        return _core.TrySetException(exception);
    }

    public bool TrySetCanceled(CancellationToken ct = default) {
        return _core.TrySetCanceled(ct);
    }

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();

    void IPandaTaskSource.OnCompleted(Action<object?> continuation, object? state, short token) {
        // Capture the awaiter's dispatcher once, on first await.
        if (_capturedDispatcher is null) {
            var d = DispatcherTable.GetCurrent();
            if (d is not null) {
                Interlocked.CompareExchange(ref _capturedDispatcher, d, null);
            }
        }

        var dispatcher = _capturedDispatcher;
        if (dispatcher is null) {
            _core.OnCompleted(continuation, state, token);
            return;
        }

        _core.OnCompleted(
            static s => {
                var (disp, cont, st) = ((TaskChainDispatcher, Action<object?>, object?))s!;
                disp.Post(() => cont(st));
            },
            (dispatcher, continuation, state),
            token);
    }

    void IPandaTaskSource.GetResult(short token) => _core.GetResult(token);
}

/// <summary>
/// Generic variant carrying a result of type <typeparamref name="T"/>.
/// </summary>
public sealed class PandaTaskCompletionSource<T> : IPandaTaskSource<T> {
    PandaTaskCompletionSourceCore<T> _core;
    TaskChainDispatcher? _capturedDispatcher;

    public PandaTaskCompletionSource() {
        _core.Reset();
    }

    public PandaTask<T> Task => new(this, _core.Version);

    public bool TrySetResult(T result) => _core.TrySetResult(result);
    public bool TrySetException(Exception exception) => _core.TrySetException(exception);
    public bool TrySetCanceled(CancellationToken ct = default) => _core.TrySetCanceled(ct);

    PandaTaskStatus IPandaTaskSource.GetStatus(short token) => _core.GetStatus(token);
    PandaTaskStatus IPandaTaskSource.UnsafeGetStatus() => _core.UnsafeGetStatus();

    void IPandaTaskSource.OnCompleted(Action<object?> continuation, object? state, short token) {
        if (_capturedDispatcher is null) {
            var d = DispatcherTable.GetCurrent();
            if (d is not null) {
                Interlocked.CompareExchange(ref _capturedDispatcher, d, null);
            }
        }

        var dispatcher = _capturedDispatcher;
        if (dispatcher is null) {
            _core.OnCompleted(continuation, state, token);
            return;
        }

        _core.OnCompleted(
            static s => {
                var (disp, cont, st) = ((TaskChainDispatcher, Action<object?>, object?))s!;
                disp.Post(() => cont(st));
            },
            (dispatcher, continuation, state),
            token);
    }

    void IPandaTaskSource.GetResult(short token) => _core.GetResult(token);
    public T GetResult(short token) => _core.GetResult(token);
}
