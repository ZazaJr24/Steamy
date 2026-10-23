using System.Windows.Threading;

namespace Steamy.Services;

public interface IDiscordSdkHost
{
    Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken token = default);
}

/// <summary>One SDK dispatcher per process. Native initialization, calls and callbacks share its STA.</summary>
internal sealed class DiscordSdkHost : IDiscordSdkHost, IDisposable
{
    internal static DiscordSdkHost Shared { get; } = new();
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    internal int ThreadId { get; private set; }
    public DiscordSdkHost()
    {
        var thread = new Thread(() =>
        {
            try
            {
                ThreadId = Environment.CurrentManagedThreadId;
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ready.TrySetResult(dispatcher); Dispatcher.Run();
            }
            catch (Exception error) { _ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Steamy Discord SDK" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public async Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dispatcher = await _ready.Task.WaitAsync(token).ConfigureAwait(false);
        return await dispatcher.InvokeAsync(operation, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = _ready.Task.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.BeginInvokeShutdown(DispatcherPriority.Background); }, TaskScheduler.Default);
    }
}
