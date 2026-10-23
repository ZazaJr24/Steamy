using System.IO;
using System.Windows.Threading;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class UcPreferencesDispatchTests
{
    private const string Saved = """{"Folder":"saved-folder","AppId":"730","NetworkAppId":"480"}""";

    [Fact]
    public void LoadingPreferencesLeavesTheDispatcherAvailableAndPublishesOnItsOwner()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            UcOnlineViewModel? model = null;
            try
            {
                var owner = Environment.CurrentManagedThreadId; var readThread = owner;
                model = Model((_, _) => { readThread = Environment.CurrentManagedThreadId; entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return Task.FromResult<string?>(Saved); });
                Assert.False(model.PreferencesLoaded.IsCompleted);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); Assert.NotEqual(owner, readThread);
                var publishedOn = 0;
                model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.Folder)) publishedOn = Environment.CurrentManagedThreadId; };
                var frame = new DispatcherFrame(); var inputRan = false; var inputSawPendingRead = false;
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    inputRan = true; inputSawPendingRead = !model.PreferencesLoaded.IsCompleted; release.Set();
                }));
                _ = model.PreferencesLoaded.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                model.PreferencesLoaded.GetAwaiter().GetResult();
                Assert.True(inputRan); Assert.True(inputSawPendingRead); Assert.Equal(owner, publishedOn);
                Assert.Equal("saved-folder", model.Folder); Assert.Equal("730", model.AppId); Assert.Equal("480", model.NetworkAppId);
                Assert.True(model.InspectCommand.CanExecute(null));
            }
            catch (Exception error) { failure = error; }
            finally { release.Set(); dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("app")]
    [InlineData("network")]
    public async Task SavedPreferencesCannotOverwriteNewInput(string edited)
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Model((_, _) => pending.Task);
        if (edited == "folder") model.Folder = "new-folder";
        else if (edited == "app") model.AppId = "42";
        else model.NetworkAppId = "99";
        pending.SetResult(Saved); await model.PreferencesLoaded;
        Assert.Equal(edited == "folder" ? "new-folder" : "", model.Folder);
        Assert.Equal(edited == "app" ? "42" : "", model.AppId);
        Assert.Equal(edited == "network" ? "99" : "480", model.NetworkAppId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData(null)]
    public async Task InvalidOrMissingPreferencesLeaveThePageUsable(string? text)
    {
        var model = Model((_, _) => Task.FromResult(text)); await model.PreferencesLoaded;
        Assert.Equal("", model.Folder); Assert.True(model.CanEdit);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("bom")]
    [InlineData("large")]
    [InlineData("missing")]
    public async Task RealPreferenceFilesRetainTheirEncodingAndSizeLimit(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-uc-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (format != "missing")
                await File.WriteAllTextAsync(Path.Combine(root, "uc-online.preferences.json"), format == "large" ? new string(' ', 8192) + Saved : Saved, new System.Text.UTF8Encoding(format == "bom"));
            var model = new UcOnlineViewModel(new UcOnlineService(new NoDownloads(), Path.Combine(root, "backups")), dataRoot: root);
            await model.PreferencesLoaded;
            Assert.Equal(format is "utf8" or "bom" ? "saved-folder" : "", model.Folder);
            Assert.Equal(format is "utf8" or "bom" ? "730" : "", model.AppId);
            Assert.Equal("480", model.NetworkAppId);
        }
        finally { Directory.Delete(root, true); }
    }

    private static UcOnlineViewModel Model(Func<string, CancellationToken, Task<string?>> reader)
        => new(new UcOnlineService(new NoDownloads(), Path.Combine(Path.GetTempPath(), "Steamy-unused-backups")), null, null, Path.GetTempPath(), reader);
    private sealed class NoDownloads : IGitHubToolDownloadService
    {
        public string? GetCachedPath(GitHubToolDefinition tool) => null;
        public bool HasCached(GitHubToolDefinition tool) => false;
        public Task<ToolDownloadResult> DownloadLatestAsync(GitHubToolDefinition tool, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
