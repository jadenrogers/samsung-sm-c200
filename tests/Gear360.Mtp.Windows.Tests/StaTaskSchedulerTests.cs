namespace Gear360.Mtp.Windows.Tests;

public sealed class StaTaskSchedulerTests
{
    [Fact]
    public async Task Runs_every_task_on_one_STA_thread()
    {
        using var scheduler = new StaTaskScheduler("test worker");

        var calls = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            scheduler.RunAsync(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState())))));

        Assert.All(calls, c => Assert.Equal(scheduler.ThreadId, c.CurrentManagedThreadId));
        Assert.All(calls, c => Assert.Equal(ApartmentState.STA, c.Item2));
        Assert.NotEqual(Environment.CurrentManagedThreadId, scheduler.ThreadId);
    }

    [Fact]
    public async Task Never_runs_two_tasks_at_once()
    {
        using var scheduler = new StaTaskScheduler("test worker");
        var running = 0;
        var maxRunning = 0;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => scheduler.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            Thread.Sleep(5);
            Interlocked.Decrement(ref running);
        })));

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Exceptions_reach_the_caller()
    {
        using var scheduler = new StaTaskScheduler("test worker");

        var ex = await Assert.ThrowsAsync<IOException>(() => scheduler.RunAsync<int>(() => throw new IOException("boom")));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_task_from_starting()
    {
        using var scheduler = new StaTaskScheduler("test worker");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(() => ran = true, cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public void Running_after_dispose_throws_ObjectDisposedException()
    {
        var scheduler = new StaTaskScheduler("test worker");
        scheduler.Dispose();

        // Throws synchronously: no task is created once the worker has stopped.
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = scheduler.RunAsync(() => 1);
        });
    }
}
