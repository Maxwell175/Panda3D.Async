using System;
using System.Collections.Concurrent;
using System.Threading;
using Interrogate;
using Panda3D.Core;

namespace Panda3D.Async.Scheduling;

/// <summary>
/// Looks up (or lazily creates) the <see cref="TaskChainDispatcher"/> for a named
/// <c>AsyncTaskChain</c>.  Keyed by name because C# <see cref="IAsyncTaskChain"/>
/// wrappers are cheap handles — two handles to the same native chain must share one
/// dispatcher.
/// </summary>
internal static class DispatcherTable {
    static readonly ConcurrentDictionary<string, TaskChainDispatcher> _byName = new();

    /// <summary>Gets or creates the dispatcher for <paramref name="chainName"/>; creates the chain on the global task manager if needed.</summary>
    public static TaskChainDispatcher GetOrCreate(string chainName) {
        return _byName.GetOrAdd(chainName, static name => {
            var mgr = AsyncTaskManager.GetGlobalPtr();
            var chain = mgr.FindTaskChain(name);
            if (chain is null) {
                chain = mgr.MakeTaskChain(name);
            }
            return new TaskChainDispatcher(chain, name);
        });
    }

    /// <summary>
    /// Get the dispatcher for the chain the current task is running
    /// on, or null if we're not inside a Panda3D task.
    /// </summary>
    public static TaskChainDispatcher? GetCurrent() {
        var current = SynchronizationContext.Current;
        if (current is TaskChainDispatcher d) return d;

        // Native-task inference is in a separate method so a failing libpanda
        // load (e.g. unit tests without the runtime) trips only on this path.
        if (_nativeLoadBroken) return null;
        try {
            return GetCurrentFromNativeTask();
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex)) {
            _nativeLoadBroken = true;
            return null;
        }
        catch (InvalidOperationException) {
            // Thread.get_current_task() returned nullptr — not in any Panda task.
            return null;
        }
    }

    static bool _nativeLoadBroken;

    static TaskChainDispatcher? GetCurrentFromNativeTask() {
        var thread = Panda3D.Core.Thread.GetCurrentThread();
        var currentTask = thread.GetCurrentTask();
        if (currentTask is null) return null;

        var task = currentTask.CastTo<AsyncTask>();
        if (task is null) return null;

        var name = task.GetTaskChain();
        if (string.IsNullOrEmpty(name)) return null;
        return GetOrCreate(name);
    }

    static bool IsNativeLoadFailure(Exception? ex) {
        while (ex is not null) {
            if (ex is DllNotFoundException) return true;
            ex = ex.InnerException;
        }
        return false;
    }
}
