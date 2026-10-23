using System.IO;
using System.Windows.Threading;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class SteamlessStatusDispatchTests
{
    [Fact]
    public void SynchronousStatusProviderDoesNotBlockTheDispatcher()
    {
        OnSta(() =>
        {
            using var service = new StatusService();
            var model = new SteamlessViewModel(service, new Settings());
            Task? probe = null;
            try
            {
                var caller = Environment.CurrentManagedThreadId;
                probe = model.RefreshStatusAsync();
                Assert.False(probe.IsCompleted);
                Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotEqual(caller, service.CheckThread);
                var frame = new DispatcherFrame(); var inputRan = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { inputRan = true; frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                Assert.True(inputRan); Assert.False(probe.IsCompleted);
            }
            finally { service.Release.Set(); probe?.GetAwaiter().GetResult(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOldSuccessOrFailureCannotReplaceANewerStatus(bool oldFails)
    {
        using var service = new StatusService { FirstFails = oldFails };
        var model = new SteamlessViewModel(service, new Settings());
        var previous = model.RefreshStatusAsync();
        try
        {
            Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
            model.SteamlessPath = "new.exe";
            await model.RefreshStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Ready to run", model.Status); Assert.Equal("new-version", model.ToolVersion);
            service.Release.Set(); await previous;
            Assert.Equal("Ready to run", model.Status); Assert.Equal("new-version", model.ToolVersion);
        }
        finally { service.Release.Set(); await previous; }
    }

    [Fact]
    public async Task ChangingThePathInvalidatesThePendingStatusEvenWithoutAnotherRefresh()
    {
        using var service = new StatusService();
        var model = new SteamlessViewModel(service, new Settings());
        var previous = model.RefreshStatusAsync();
        try
        {
            Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
            model.SteamlessPath = "unverified.exe";
            service.Release.Set(); await previous;
            Assert.Equal("Not checked", model.Status); Assert.Equal("—", model.ToolVersion);
        }
        finally { service.Release.Set(); await previous; }
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new() { SteamlessExePath = "old.exe" };
        public Task SaveAsync(AppSettings value, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class StatusService : ISteamlessService, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        internal int CheckThread; internal bool FirstFails;
        public Task<SteamlessToolStatus> CheckAsync(string path, CancellationToken token = default)
        {
            if (path == "old.exe")
            {
                CheckThread = Environment.CurrentManagedThreadId; Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (FirstFails) throw new IOException("Old path unavailable");
            }
            return Task.FromResult(new SteamlessToolStatus(true, path, "Ready", path == "old.exe" ? "old-version" : "new-version", DateTime.Now));
        }
        public Task<SteamlessRunResult> RunAsync(SteamlessRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SteamlessRunResult> UndoAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var worker = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
