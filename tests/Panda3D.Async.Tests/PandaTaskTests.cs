using System;
using System.Threading.Tasks;
using Panda3D.Async;
using Xunit;

namespace Panda3D.Async.Tests;

public class PandaTaskTests {
    [Fact]
    public void CompletedTask_is_sync_done() {
        var t = PandaTask.CompletedTask;
        Assert.True(t.GetAwaiter().IsCompleted);
        t.GetAwaiter().GetResult();  // doesn't throw
    }

    [Fact]
    public async Task Await_sync_async_method_completes() {
        var v = await TrivialAsync();
        Assert.Equal(42, v);

        static async PandaTask<int> TrivialAsync() {
            return 42;
        }
    }

    [Fact]
    public async Task Tcs_set_result_resumes_awaiter() {
        var tcs = new PandaTaskCompletionSource<int>();

        // Kick off an awaiter on another thread — it'll suspend.
        var outcome = new TaskCompletionSource<int>();
        _ = Task.Run(async () => {
            try {
                var v = await AwaitValueAsync(tcs.Task);
                outcome.TrySetResult(v);
            } catch (Exception ex) { outcome.TrySetException(ex); }
        });

        await Task.Delay(50);  // give the awaiter time to hit the await
        if (outcome.Task.IsCompleted) {
            // Unexpected — surface the underlying exception if any.
            await outcome.Task;  // rethrows
            Assert.Fail($"Awaiter completed prematurely with result {outcome.Task.Result}");
        }

        tcs.TrySetResult(7);
        var r = await outcome.Task;
        Assert.Equal(7, r);

        static async PandaTask<int> AwaitValueAsync(PandaTask<int> t) {
            var v = await t;
            return v;
        }
    }

    [Fact]
    public async Task Tcs_set_exception_rethrows_from_await() {
        var tcs = new PandaTaskCompletionSource();
        tcs.TrySetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            await tcs.Task;
        });
    }

    [Fact]
    public async Task Tcs_set_canceled_throws_OperationCanceled() {
        var tcs = new PandaTaskCompletionSource();
        tcs.TrySetCanceled();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => {
            await tcs.Task;
        });
    }

    [Fact]
    public void Status_is_Succeeded_for_CompletedTask() {
        Assert.Equal(PandaTaskStatus.Succeeded, PandaTask.CompletedTask.Status);
    }

    [Fact]
    public void Status_is_Pending_for_fresh_tcs() {
        var tcs = new PandaTaskCompletionSource();
        Assert.Equal(PandaTaskStatus.Pending, tcs.Task.Status);
    }
}
