using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordSdkHostTests
{
    [Fact]
    public async Task SdkOperationsKeepOneStaOwnerWhileAsyncRequestsLeaveItsQueueAvailable()
    {
        using var host = new DiscordSdkHost();
        var waiting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = await host.InvokeAsync(() => waiting.Task);
        var owner = await host.InvokeAsync(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        Assert.Equal(host.ThreadId, owner.Item1); Assert.Equal(ApartmentState.STA, owner.Item2);
        Assert.False(pending.IsCompleted);
        waiting.SetResult(42); Assert.Equal(42, await pending);
        Assert.Equal(owner.Item1, await host.InvokeAsync(() => Environment.CurrentManagedThreadId));
    }
    [Fact]
    public async Task QueuedCancellationDoesNotExecuteAnObsoleteNativeOperation()
    {
        using var host = new DiscordSdkHost();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var executed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InvokeAsync(() => executed = true, cancel.Token));
        Assert.False(executed);
    }
}
