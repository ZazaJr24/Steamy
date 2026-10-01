using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadQueueSchedulerTests
{
    private sealed class WaitingJob(int id, int priority = 1, long position = 1)
    {
        public int Id { get; } = id;
        public int Priority { get; set; } = priority;
        public long Position { get; } = position;
        public bool Waiting { get; set; } = true;
    }

    [Fact]
    public async Task SelectsHighPriorityThenUserOrder()
    {
        WaitingJob[] jobs = [new(1, 0), new(2, 2, 4), new(3, 2, 2), new(4, 1)];
        var started = new List<int>();
        await DownloadQueueScheduler.RunAsync(() => jobs.Where(job => job.Waiting), job => job.Priority,
            job => job.Position, job => { started.Add(job.Id); job.Waiting = false; return Task.CompletedTask; }, 1);
        Assert.Equal([3, 2, 4, 1], started);
    }

    [Fact]
    public async Task RechecksCancellationAndPriorityWhenSlotOpens()
    {
        WaitingJob[] jobs = [new(1, 2), new(2, 1), new(3, 0), new(4, 1, 2)];
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<int>();
        var queue = DownloadQueueScheduler.RunAsync(() => jobs.Where(job => job.Waiting), job => job.Priority,
            job => job.Position, job =>
            {
                started.Add(job.Id);
                job.Waiting = false;
                return job.Id == 1 ? first.Task : Task.CompletedTask;
            }, 1);
        Assert.Equal([1], started);
        jobs[1].Waiting = false; // Cancelled or removed before a process was started.
        jobs[2].Priority = 2; // A priority change while another download is running takes effect.
        first.SetResult();
        await queue.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([1, 3, 4], started);
    }

    [Fact]
    public async Task ParallelQueueNeverStartsMoreThanItsConfiguredSlots()
    {
        var jobs = Enumerable.Range(0, 5).Select(id => new WaitingJob(id)).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var queue = DownloadQueueScheduler.RunAsync(() => jobs.Where(job => job.Waiting), job => job.Priority,
            job => job.Position, async job =>
            {
                job.Waiting = false;
                var count = Interlocked.Increment(ref active);
                peak = Math.Max(peak, count);
                await gate.Task;
                Interlocked.Decrement(ref active);
            }, 2);
        Assert.Equal(2, active);
        gate.SetResult();
        await queue.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, peak);
        Assert.All(jobs, job => Assert.False(job.Waiting));
    }

    [Fact]
    public async Task StoppingQueueDoesNotCancelAStartedDownloadOrStartWaitingJobs()
    {
        WaitingJob[] jobs = [new(1), new(2)];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var queue = DownloadQueueScheduler.RunAsync(() => jobs.Where(job => job.Waiting), job => job.Priority,
            job => job.Position, job => { job.Waiting = false; return gate.Task; }, 1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue);
        Assert.False(gate.Task.IsCanceled);
        Assert.True(jobs[1].Waiting);
        gate.SetResult();
    }

    [Fact]
    public async Task ExistingDownloadsOccupySlotsUntilTheyFinish()
    {
        var jobs = new[] { new WaitingJob(1), new WaitingJob(2) };
        var external = 1;
        var started = new List<int>();
        var queue = DownloadQueueScheduler.RunAsync(() => jobs.Where(job => job.Waiting), job => job.Priority,
            job => job.Position, job => { started.Add(job.Id); job.Waiting = false; return Task.CompletedTask; },
            1, externallyActiveJobs: () => Volatile.Read(ref external));
        Assert.Empty(started);
        Interlocked.Exchange(ref external, 0);
        await queue.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([1, 2], started);
    }

    [Fact]
    public async Task QueueCanStopWhileEverySlotBelongsToAnotherDownload()
    {
        var job = new WaitingJob(1);
        using var cancellation = new CancellationTokenSource();
        var queue = DownloadQueueScheduler.RunAsync(() => new[] { job }, item => item.Priority,
            item => item.Position, _ => throw new InvalidOperationException("Must not start"),
            1, cancellation.Token, () => 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue);
        Assert.True(job.Waiting);
    }

    [Fact]
    public async Task AStopDuringAnImmediateStartPreventsTheNextStart()
    {
        var jobs = new[] { new WaitingJob(1), new WaitingJob(2) };
        using var cancellation = new CancellationTokenSource();
        var started = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadQueueScheduler.RunAsync(
            () => jobs, job => job.Priority, job => job.Position, job =>
            {
                started.Add(job.Id);
                cancellation.Cancel();
                return Task.CompletedTask;
            }, 2, cancellation.Token));
        Assert.Equal([1], started);
    }
}
