using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Panda3D.Async.Scheduling;

/// <summary>
/// Static <see cref="UnmanagedCallersOnlyAttribute"/> trampolines passed to
/// <c>ManagedAsyncTask.make</c> as the run / free callbacks.  The user_data is a
/// pinned <see cref="GCHandle"/> identifying the managed <see cref="IManagedCallback"/>;
/// the C++ side owns it and invokes <see cref="FreeCallback"/> on destruction.
/// </summary>
internal static unsafe class ManagedTrampolines {
    /// <summary>Raw address of <see cref="RunCallback"/> — passed as <c>ulong</c> so the interrogate binding takes it verbatim.</summary>
    public static ulong RunFnPtr => (ulong)(nint)(delegate* unmanaged<IntPtr, int>)&RunCallback;

    /// <summary>Raw address of <see cref="FreeCallback"/>.</summary>
    public static ulong FreeFnPtr => (ulong)(nint)(delegate* unmanaged<IntPtr, void>)&FreeCallback;

    [UnmanagedCallersOnly]
    static int RunCallback(IntPtr userData) {
        try {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is IManagedCallback cb) {
                // Pin SyncContext to the chain dispatcher for cb.Run() so synchronous
                // continuations fired from DelaySource/WhenAllFuturesSource (etc.) that
                // later `await` something capture the right context and resume on-chain.
                var dispatcher = DispatcherTable.GetCurrent();
                if (dispatcher is null
                    || ReferenceEquals(SynchronizationContext.Current, dispatcher)) {
                    return cb.Run();
                }
                var prev = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(dispatcher);
                try {
                    return cb.Run();
                } finally {
                    SynchronizationContext.SetSynchronizationContext(prev);
                }
            }
            return (int)Panda3D.Core.AsyncTask_DoneStatus.DS_done;
        }
        catch (Exception ex) {
            PandaTaskScheduler.PublishUnobservedException(ex);
            return (int)Panda3D.Core.AsyncTask_DoneStatus.DS_done;
        }
    }

    [UnmanagedCallersOnly]
    static void FreeCallback(IntPtr userData) {
        try {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is IManagedCallback cb) {
                cb.Dispose();
            }
            handle.Free();
        }
        catch (Exception ex) {
            PandaTaskScheduler.PublishUnobservedException(ex);
        }
    }
}

/// <summary>
/// Managed side of a <c>ManagedAsyncTask</c> callback.  The C++ task
/// holds a <see cref="GCHandle"/> to an implementation; <see cref="Run"/>
/// is invoked every epoch and must return a
/// <see cref="Panda3D.Core.AsyncTask_DoneStatus"/> value cast to int.
/// </summary>
internal interface IManagedCallback : IDisposable {
    /// <summary>Run once per chain epoch; return DoneStatus as int.</summary>
    int Run();
}
