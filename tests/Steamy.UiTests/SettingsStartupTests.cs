using System.IO;
using Steamy.Services;
using System.Windows.Threading;

namespace Steamy.UiTests;

public sealed class SettingsStartupTests
{
    [Fact]
    public void ToolDiscoveryPublishesOnTheOwningDispatcherWhileInputRemainsAvailable()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "steamy-start-dispatch-" + Guid.NewGuid().ToString("N"), "settings.json");
                var service = new JsonSettingsService(path, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return "worker-found.exe"; });
                var owner = Environment.CurrentManagedThreadId; var publishedOn = 0;
                var settings = service.Load();
                settings.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(settings.DepotDownloaderPath)) publishedOn = Environment.CurrentManagedThreadId; };
                var task = service.WarmToolPathAsync(); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var frame = new DispatcherFrame(); var inputSawPending = false;
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { inputSawPending = !task.IsCompleted; release.Set(); }));
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
                Assert.True(inputSawPending); Assert.Equal(owner, publishedOn);
            }
            catch (Exception error) { failure = error; }
            finally { release.Set(); dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public async Task AppearanceLoadDoesNotRunToolDiscoveryAndWarmCallersShareAWorker()
    {
        var path = Path.Combine(Path.GetTempPath(), "steamy-settings-start-" + Guid.NewGuid().ToString("N"), "settings.json");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0; var thread = 0;
        var service = new JsonSettingsService(path, () =>
        {
            Interlocked.Increment(ref calls); thread = Environment.CurrentManagedThreadId;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return "detected-tool.exe";
        });
        var caller = Environment.CurrentManagedThreadId;
        var settings = service.Load(); Assert.Equal(0, calls);
        var warm = service.WarmToolPathAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(caller, thread); Assert.False(warm.IsCompleted);
            Assert.Same(warm, service.WarmToolPathAsync()); Assert.Same(settings, service.Load());
        }
        finally { release.Set(); }
        await warm; Assert.Equal(1, calls); Assert.Equal("detected-tool.exe", settings.DepotDownloaderPath);
        Assert.False(File.Exists(path)); // Detection retains the prior in-memory-only behavior.
    }

    [Fact]
    public async Task AUserPathEditWinsOverADelayedAutomaticDiscovery()
    {
        var path = Path.Combine(Path.GetTempPath(), "steamy-settings-start-" + Guid.NewGuid().ToString("N"), "settings.json");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var service = new JsonSettingsService(path, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return "automatic.exe"; });
        var settings = service.Load(); var warm = service.WarmToolPathAsync();
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); settings.DepotDownloaderPath = "user-selected.exe"; }
        finally { release.Set(); }
        await warm; Assert.Equal("user-selected.exe", settings.DepotDownloaderPath);
    }
}
