using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Panda3D.Async.CompilerServices;

/// <summary>
/// Reusable promise core embedded by value inside every pooled
/// <see cref="IPandaTaskSource"/> (state-machine runners, delay sources, yield
/// sources).  <see cref="Reset"/> bumps <see cref="Version"/> so an awaiter
/// holding a stale token can't resume against a recycled source.
/// </summary>
public struct PandaTaskCompletionSourceCore<T> {
    short _version;
    // int because Interlocked.CompareExchange has no byte overload.
    int _status;
    T _result;
    ExceptionDispatchInfo? _error;
    Action<object?>? _continuation;
    object? _continuationState;

    public short Version => _version;

    /// <summary>Prepare the core for reuse.  Bumps version and clears state.</summary>
    public void Reset() {
        _version = (short)unchecked(_version + 1);
        if (_version == 0) _version = 1;  // skip the "sync-completed" sentinel
        _status = (int)PandaTaskStatus.Pending;
        _result = default!;
        _error = null;
        _continuation = null;
        _continuationState = null;
    }

    public PandaTaskStatus GetStatus(short token) {
        ValidateToken(token);
        return (PandaTaskStatus)_status;
    }

    public PandaTaskStatus UnsafeGetStatus() => (PandaTaskStatus)_status;

    public bool TrySetResult(T result) {
        if (Interlocked.CompareExchange(ref _status,
                (int)PandaTaskStatus.Succeeded,
                (int)PandaTaskStatus.Pending) == (int)PandaTaskStatus.Pending) {
            _result = result;
            InvokeContinuation();
            return true;
        }
        return false;
    }

    public bool TrySetException(Exception ex) {
        var target = ex is OperationCanceledException
            ? (int)PandaTaskStatus.Canceled
            : (int)PandaTaskStatus.Faulted;
        if (Interlocked.CompareExchange(ref _status, target,
                (int)PandaTaskStatus.Pending) == (int)PandaTaskStatus.Pending) {
            _error = ExceptionDispatchInfo.Capture(ex);
            InvokeContinuation();
            return true;
        }
        return false;
    }

    public bool TrySetCanceled(CancellationToken ct = default) {
        if (Interlocked.CompareExchange(ref _status,
                (int)PandaTaskStatus.Canceled,
                (int)PandaTaskStatus.Pending) == (int)PandaTaskStatus.Pending) {
            _error = ExceptionDispatchInfo.Capture(new OperationCanceledException(ct));
            InvokeContinuation();
            return true;
        }
        return false;
    }

    /// <summary>Get the result, rethrowing any stored exception.</summary>
    public T GetResult(short token) {
        ValidateToken(token);
        switch ((PandaTaskStatus)_status) {
            case PandaTaskStatus.Succeeded:
                return _result;
            case PandaTaskStatus.Canceled:
            case PandaTaskStatus.Faulted:
                _error!.Throw();
                return default!;  // unreachable
            default:
                throw new InvalidOperationException("PandaTask has not yet completed.");
        }
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token) {
        ValidateToken(token);
        if ((PandaTaskStatus)_status != PandaTaskStatus.Pending) {
            continuation(state);
            return;
        }

        if (Interlocked.CompareExchange(ref _continuation, continuation, null) != null) {
            throw new InvalidOperationException("PandaTaskSource does not support multi-await.");
        }
        _continuationState = state;

        // Race resolution: TrySetX may have flipped status after our snapshot but
        // before our CAS — re-check and fire inline so the continuation isn't lost.
        if ((PandaTaskStatus)_status != PandaTaskStatus.Pending) {
            var c = Interlocked.Exchange(ref _continuation, null);
            if (c is not null) c(_continuationState);
        }
    }

    void InvokeContinuation() {
        var c = Interlocked.Exchange(ref _continuation, null);
        if (c is not null) c(_continuationState);
    }

    void ValidateToken(short token) {
        if (token != _version) {
            throw new InvalidOperationException(
                "Stale PandaTask token — source has been reset and recycled.");
        }
    }
}
