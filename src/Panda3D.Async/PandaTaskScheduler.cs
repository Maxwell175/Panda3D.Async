using System;
using Panda3D.Async.Scheduling;

namespace Panda3D.Async;

/// <summary>
/// Global sink for unobserved exceptions from <see cref="PandaTask.Forget"/>
/// and GC-path exception surfacing.  Mirrors UniTask's
/// <c>UniTaskScheduler.UnobservedTaskException</c>.
/// </summary>
public static class PandaTaskScheduler {
    /// <summary>
    /// Fired when a <see cref="PandaTask"/> is dropped with an
    /// unobserved exception.  <see cref="OperationCanceledException"/>
    /// is never published.
    /// </summary>
    public static event Action<Exception>? UnobservedException;

    internal static void PublishUnobservedException(Exception ex) {
        UnobservedException?.Invoke(ex);
    }

    /// <summary>
    /// Name of the chain the current code is executing on, or
    /// <see langword="null"/> if not running inside any Panda3D task
    /// chain (e.g. a thread-pool worker, the main thread outside of
    /// <c>taskManager.Poll()</c>, or a unit-test environment without
    /// libpanda).  Useful for assertions in stress tests and for
    /// diagnosing chain-confusion bugs.
    /// </summary>
    public static string? CurrentChainName()
        => DispatcherTable.GetCurrent()?.ChainName;
}
