using System;
using System.Threading;
using System.Threading.Tasks;
using Panda3D.Async;
using Xunit;

namespace Panda3D.Async.Tests;

public class InteropTests {
    [Fact]
    public async Task Cross_thread_TrySetResult_is_observed_by_awaiter() {
        var tcs = new PandaTaskCompletionSource<string>();
        var result = "";

        var awaiter = Task.Run(async () => {
            result = await tcs.Task;
        });

        await Task.Delay(25);
        Assert.False(awaiter.IsCompleted);

        new Thread(() => tcs.TrySetResult("hello")).Start();

        await awaiter;
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task PandaTask_AsTask_roundtrip() {
        var tcs = new PandaTaskCompletionSource<int>();
        var task = tcs.Task.AsTask();

        Assert.False(task.IsCompleted);
        tcs.TrySetResult(99);
        var r = await task;
        Assert.Equal(99, r);
    }

    [Fact]
    public async Task FromTask_wraps_completed_Task() {
        var source = Task.FromResult(123);
        var pt = PandaTask.FromTask(source);
        Assert.Equal(PandaTaskStatus.Succeeded, pt.Status);
        var r = await pt;
        Assert.Equal(123, r);
    }

    [Fact]
    public async Task FromTask_wraps_faulted_Task() {
        var source = Task.FromException(new InvalidOperationException("sync-fault"));
        var pt = PandaTask.FromTask(source);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            await pt;
        });
    }

    [Fact]
    public async Task Await_System_Task_inside_PandaTask_coroutine_completes() {
        var outcome = await RunAsync();
        Assert.Equal("done", outcome);

        static async PandaTask<string> RunAsync() {
            await Task.Delay(10).ConfigureAwait(false);
            return "done";
        }
    }

    [Fact]
    public async Task Multiple_awaits_in_same_method_work() {
        var result = await SequencedAsync();
        Assert.Equal(6, result);

        static async PandaTask<int> SequencedAsync() {
            var tcs1 = new PandaTaskCompletionSource<int>();
            var tcs2 = new PandaTaskCompletionSource<int>();
            _ = Task.Run(() => { Thread.Sleep(5); tcs1.TrySetResult(1); });
            _ = Task.Run(() => { Thread.Sleep(10); tcs2.TrySetResult(5); });

            var a = await tcs1.Task;
            var b = await tcs2.Task;
            return a + b;
        }
    }

    [Fact]
    public void Sync_completed_await_has_zero_allocations() {
        // Debug builds box async state machines into classes; the
        // struct-box optimization only kicks in under Release.  In
        // Debug we accept ~48 bytes per call (the boxed state
        // machine) as an unavoidable cost.
#if DEBUG
        const long maxPerIter = 64;
#else
        const long maxPerIter = 8;
#endif

        for (var i = 0; i < 10; i++) _ = Warmup();

        const int iterations = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) {
            _ = Warmup();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        var delta = after - before;
        var perIter = delta / (double)iterations;
        Assert.True(perIter < maxPerIter,
            $"Expected <{maxPerIter} bytes per iteration, got {perIter:F1} (total {delta} over {iterations})");

        static async PandaTask<int> Warmup() {
            return 42;
        }
    }

    [Fact]
    public void Forget_suppresses_OperationCanceledException() {
        var tcs = new PandaTaskCompletionSource();
        Exception? seen = null;
        PandaTaskScheduler.UnobservedException += ex => seen = ex;
        try {
            tcs.TrySetCanceled();
            tcs.Task.Forget();
        } finally {
            PandaTaskScheduler.UnobservedException -= ex => seen = ex;
        }
        Assert.Null(seen);
    }

    [Fact]
    public void Forget_surfaces_regular_exceptions() {
        var tcs = new PandaTaskCompletionSource();
        Exception? seen = null;
        Action<Exception> handler = ex => seen = ex;
        PandaTaskScheduler.UnobservedException += handler;
        try {
            tcs.TrySetException(new InvalidOperationException("dropped"));
            tcs.Task.Forget();
        } finally {
            PandaTaskScheduler.UnobservedException -= handler;
        }
        Assert.IsType<InvalidOperationException>(seen);
    }
}
