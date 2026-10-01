using Steamy.Services;

namespace Steamy.Tests;

public sealed class BoundedLogBufferTests
{
    private sealed record Entry(int Id, bool Important = false);

    [Fact]
    public void BurstsRemainBoundedAndReportOmittedRoutineOutput()
    {
        var buffer = new BoundedLogBuffer<Entry>(1000, entry => entry.Important);
        for (var i = 0; i < 10_000; i++) buffer.Enqueue(new(i));
        Assert.Equal(1000, buffer.PendingCount);
        var batch = buffer.Drain(1000);
        Assert.Equal(9000, batch.OmittedRoutine);
        Assert.Equal(0, batch.OmittedImportant);
        Assert.Equal(Enumerable.Range(9000, 1000), batch.Items.Select(entry => entry.Id));
        Assert.Equal(0, buffer.PendingCount);
        Assert.Equal(0, buffer.Drain(10).OmittedRoutine);
    }

    [Fact]
    public void RoutineOutputCannotEvictQueuedErrors()
    {
        var buffer = new BoundedLogBuffer<Entry>(3, entry => entry.Important);
        buffer.Enqueue(new(1, true));
        buffer.Enqueue(new(2, true));
        buffer.Enqueue(new(3, true));
        buffer.Enqueue(new(4));
        var batch = buffer.Drain(3);
        Assert.Equal(new[] { 1, 2, 3 }, batch.Items.Select(entry => entry.Id));
        Assert.Equal(1, batch.OmittedRoutine);
        Assert.Equal(0, batch.OmittedImportant);
    }

    [Fact]
    public void ImportantOutputDisplacesRoutineOutputAndKeepsFifoOrder()
    {
        var buffer = new BoundedLogBuffer<Entry>(3, entry => entry.Important);
        buffer.Enqueue(new(1, true));
        buffer.Enqueue(new(2));
        buffer.Enqueue(new(3));
        buffer.Enqueue(new(4, true));
        var batch = buffer.Drain(3);
        Assert.Equal(new[] { 1, 3, 4 }, batch.Items.Select(entry => entry.Id));
        Assert.Equal(1, batch.OmittedRoutine);
        Assert.Equal(0, batch.OmittedImportant);
    }

    [Fact]
    public void CriticalOnlyOverflowIsExplicitRatherThanSilent()
    {
        var buffer = new BoundedLogBuffer<Entry>(2, entry => entry.Important);
        buffer.Enqueue(new(1, true));
        buffer.Enqueue(new(2, true));
        buffer.Enqueue(new(3, true));
        var batch = buffer.Drain(1);
        Assert.Equal(2, Assert.Single(batch.Items).Id);
        Assert.Equal(1, batch.OmittedImportant);
        Assert.Equal(3, Assert.Single(buffer.Drain(1).Items).Id);
    }

    [Fact]
    public async Task ConcurrentProducersDoNotLoseCountsOrExceedCapacity()
    {
        var buffer = new BoundedLogBuffer<Entry>(100, entry => entry.Important);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) buffer.Enqueue(new(producer * 1000 + i));
        })));
        var batch = buffer.Drain(100);
        Assert.Equal(100, batch.Items.Count);
        Assert.Equal(9900, batch.OmittedRoutine);
        Assert.Equal(100, batch.Items.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void DrainingBeforeOverflowKeepsTheRoutineIndexConsistent()
    {
        var buffer = new BoundedLogBuffer<Entry>(3, entry => entry.Important);
        buffer.Enqueue(new(1));
        buffer.Enqueue(new(2, true));
        buffer.Enqueue(new(3));
        Assert.Equal(1, Assert.Single(buffer.Drain(1).Items).Id);
        buffer.Enqueue(new(4, true));
        buffer.Enqueue(new(5));
        var batch = buffer.Drain(3);
        Assert.Equal(new[] { 2, 4, 5 }, batch.Items.Select(entry => entry.Id));
        Assert.Equal(1, batch.OmittedRoutine);
        Assert.Equal(0, batch.OmittedImportant);
    }

    [Fact]
    public void FullImportantBacklogRejectsLargeRoutineBurstsWithoutChangingHistory()
    {
        var buffer = new BoundedLogBuffer<Entry>(1000, entry => entry.Important);
        for (var i = 0; i < 1000; i++) buffer.Enqueue(new(i, true));
        for (var i = 0; i < 10_000; i++) buffer.Enqueue(new(1000 + i));
        var batch = buffer.Drain(1000);
        Assert.Equal(Enumerable.Range(0, 1000), batch.Items.Select(entry => entry.Id));
        Assert.Equal(10_000, batch.OmittedRoutine);
        Assert.Equal(0, batch.OmittedImportant);
    }
}
