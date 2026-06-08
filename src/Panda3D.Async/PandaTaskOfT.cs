using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Panda3D.Async.CompilerServices;
using Panda3D.Async.Scheduling;

namespace Panda3D.Async;

/// <summary>
/// Generic variant of <see cref="PandaTask"/> carrying a result of
/// type <typeparamref name="T"/>.  When <c>_source</c> is null the
/// task is synchronously completed with <c>_result</c>.
/// </summary>
[AsyncMethodBuilder(typeof(AsyncPandaTaskMethodBuilder<>))]
public readonly struct PandaTask<T> : IEquatable<PandaTask<T>> {
    readonly IPandaTaskSource<T>? _source;
    readonly T _result;
    readonly short _token;

    public PandaTask(T result) {
        _source = null;
        _result = result;
        _token = 0;
    }

    public PandaTask(IPandaTaskSource<T> source, short token) {
        _source = source;
        _result = default!;
        _token = token;
    }

    public PandaTaskStatus Status
        => _source?.GetStatus(_token) ?? PandaTaskStatus.Succeeded;

    public Awaiter GetAwaiter() => new(this);

    public ConfiguredPandaTaskAwaitable<T> ConfigureAwait(bool continueOnCapturedContext)
        => new(this, continueOnCapturedContext);

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
                var (src, tok) = ((IPandaTaskSource<T>, short))s!;
                try { src.GetResult(tok); }
                catch (OperationCanceledException) { /* silent */ }
                catch (Exception ex) {
                    PandaTaskScheduler.PublishUnobservedException(ex);
                }
            }, (source, _token), _token);
        }
    }

    public bool Equals(PandaTask<T> other)
        => ReferenceEquals(_source, other._source) && _token == other._token
           && EqualityComparer<T>.Default.Equals(_result, other._result);

    public override bool Equals(object? obj) => obj is PandaTask<T> t && Equals(t);
    public override int GetHashCode()
        => _source is null
            ? (_result is null ? 0 : _result.GetHashCode())
            : HashCode.Combine(RuntimeHelpers.GetHashCode(_source), _token);

    public readonly struct Awaiter : ICriticalNotifyCompletion {
        readonly PandaTask<T> _task;
        internal Awaiter(PandaTask<T> task) { _task = task; }

        public bool IsCompleted
            => _task._source is null
            || _task._source.GetStatus(_task._token).IsCompleted();

        public T GetResult()
            => _task._source is null
                ? _task._result
                : _task._source.GetResult(_task._token);

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

public readonly struct ConfiguredPandaTaskAwaitable<T> {
    readonly PandaTask<T> _task;
    readonly bool _continueOnCapturedContext;

    internal ConfiguredPandaTaskAwaitable(PandaTask<T> task, bool continueOnCapturedContext) {
        _task = task;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    public Awaiter GetAwaiter() => new(_task, _continueOnCapturedContext);

    public readonly struct Awaiter : ICriticalNotifyCompletion {
        readonly PandaTask<T> _task;
        readonly bool _continueOnCapturedContext;

        internal Awaiter(PandaTask<T> task, bool continueOnCapturedContext) {
            _task = task;
            _continueOnCapturedContext = continueOnCapturedContext;
        }

        public bool IsCompleted => _task.GetAwaiter().IsCompleted;
        public T GetResult() => _task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation) {
            if (_continueOnCapturedContext) {
                _task.GetAwaiter().OnCompleted(continuation);
            } else {
                using var _ = PandaSynchronizationContext.SuppressCapture();
                _task.GetAwaiter().OnCompleted(continuation);
            }
        }

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}
