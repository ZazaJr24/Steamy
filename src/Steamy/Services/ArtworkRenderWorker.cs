using System.Windows.Threading;

namespace Steamy.Services;

/// <summary>One lazy STA for frozen artwork rendering, without blocking the window dispatcher.</summary>
internal static class ArtworkRenderWorker
{
    private static readonly Lazy<Task<Dispatcher>> Worker = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ready.TrySetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Steamy artwork rendering", Priority = ThreadPriority.Normal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
    });

    internal static async Task<T> RunAsync<T>(Func<T> render, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dispatcher = await Worker.Value.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(render, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
    }
}
