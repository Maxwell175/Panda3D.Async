using System;
using System.Runtime.CompilerServices;
using Panda3D.Async.CompilerServices;
using Panda3D.Async.Scheduling;

namespace Panda3D.Async;

/// <summary>
/// A lightweight struct task, analogous to UniTask's <c>UniTask</c>.
/// Holds an optional <see cref="IPandaTaskSource"/> plus a version
/// token; when the source is null the task is already completed
/// successfully and zero allocations are required on the fast path.
/// </summary>
/// <remarks>
/// Continuations default to resuming on the originating
/// <c>AsyncTaskChain</c>.  Use <see cref="ConfigureAwait"/> to opt out.
/// </remarks>
[AsyncMethodBuilder(typeof(AsyncPandaTaskMethodBuilder))]
public readonly partial struct PandaTask : IEquatable<PandaTask> {
    readonly IPandaTaskSource? _source;
    readonly short _token;

    public PandaTask(IPandaTaskSource source, short token) {
        _source = source;
        _token = token;
    }

    /// <summary>
    /// A <see cref="PandaTask"/> already in the <c>Succeeded</c> state.
    /// No allocation.
    /// </summary>
    public static PandaTask CompletedTask => default;

    public PandaTaskStatus Status
        => _source?.GetStatus(_token) ?? PandaTaskStatus.Succeeded;

    public Awaiter GetAwaiter() => new(this);

    /// <summary>
    /// Opts out of same-chain resumption.  When awaited, the
    /// continuation runs on whatever thread signalled the source —
    /// useful for pure-CPU follow-up work that doesn't touch the
    /// scene graph.
    /// </summary>
    public ConfiguredPandaTaskAwaitable ConfigureAwait(bool continueOnCapturedContext)
        => new(this, continueOnCapturedContext);

    /// <summary>
    /// Explicitly acknowledge this task as fire-and-forget.  Any
    /// unobserved exception is forwarded to
    /// <see cref="PandaTaskScheduler.UnobservedException"/>.
    /// Cancellation is silently swallowed.
    /// </summary>
    public void Forget() {
        var source = _source;
        if (source is null) return;
        if (source.GetStatus(_token).IsCompleted()) {
            try { source.GetResult(_token); }
            catch (OperationCanceledException) { /* silent */ }
            catch (Exception ex) {
                PandaTaskScheduler.PublishUnobservedException(ex);
            }
        } else {
            source.OnCompleted(static (s) => {
                var (src, tok) = ((IPandaTaskSource, short))s!;
                try { src.GetResult(tok); }
                catch (OperationCanceledException) { /* silent */ }
                catch (Exception ex) {
                    PandaTaskScheduler.PublishUnobservedException(ex);
                }
            }, (source, _token), _token);
        }
    }

    public bool Equals(PandaTask other)
        => ReferenceEquals(_source, other._source) && _token == other._token;

    public override bool Equals(object? obj) => obj is PandaTask t && Equals(t);
    public override int GetHashCode()
        => _source is null ? 0 : HashCode.Combine(RuntimeHelpers.GetHashCode(_source), _token);

    /// <summary>
    /// Awaiter returned by <see cref="GetAwaiter"/>.  Struct — no
    /// allocation on the fast path.
    /// </summary>
    public readonly struct Awaiter : ICriticalNotifyCompletion {
        readonly PandaTask _task;
        internal Awaiter(PandaTask task) { _task = task; }

        public bool IsCompleted
            => _task._source is null
            || _task._source.GetStatus(_task._token).IsCompleted();

        public void GetResult() => _task._source?.GetResult(_task._token);

        public void OnCompleted(Action continuation) {
            if (_task._source is null) {
                continuation();
                return;
            }
            _task._source.OnCompleted(PandaTaskCallbacks.InvokeAction,
                                       continuation, _task._token);
        }

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}

/// <summary>
/// Result of <see cref="PandaTask.ConfigureAwait"/>.  Wraps the
/// underlying task with a flag controlling whether the continuation
/// captures the current <c>TaskChainDispatcher</c>.
/// </summary>
public readonly struct ConfiguredPandaTaskAwaitable {
    readonly PandaTask _task;
    readonly bool _continueOnCapturedContext;

    internal ConfiguredPandaTaskAwaitable(PandaTask task, bool continueOnCapturedContext) {
        _task = task;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    public Awaiter GetAwaiter() => new(_task, _continueOnCapturedContext);

    public readonly struct Awaiter : ICriticalNotifyCompletion {
        readonly PandaTask _task;
        readonly bool _continueOnCapturedContext;

        internal Awaiter(PandaTask task, bool continueOnCapturedContext) {
            _task = task;
            _continueOnCapturedContext = continueOnCapturedContext;
        }

        public bool IsCompleted => _task.GetAwaiter().IsCompleted;
        public void GetResult() => _task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation) {
            if (_continueOnCapturedContext) {
                _task.GetAwaiter().OnCompleted(continuation);
            } else {
                // Bypass the dispatcher — run wherever the source
                // fires the callback.  The caller must not touch
                // scene-graph state without re-hopping to a chain.
                using var _ = PandaSynchronizationContext.SuppressCapture();
                _task.GetAwaiter().OnCompleted(continuation);
            }
        }

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}

internal static class PandaTaskCallbacks {
    public static readonly Action<object?> InvokeAction = static o => ((Action)o!)();
}
