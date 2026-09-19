using System.Collections.ObjectModel;
using SteamContentManager.Models;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class HonestDownloadTests
{
    private static (DownloadManager Manager, AppDataStore Store, StubDepotDownloader Downloader) CreateManager(AppSettings settings)
    {
        var store = new AppDataStore();
        var logging = new InMemoryLoggingService(store, new NullLocalDatabase());
        var downloader = new StubDepotDownloader();
        var manager = new DownloadManager(
            store,
            new StubSettingsService(settings),
            downloader,
            new LocalFileVerificationService(),
            new StubQueueStore(),
            logging,
            new DemoNotificationService());

        return (manager, store, downloader);
    }

    [Fact]
    public async Task StartAsync_WithoutConfiguredToolFailsInsteadOfSimulatingProgress()
    {
        var (manager, store, downloader) = CreateManager(new AppSettings());
        var job = new DownloadJob { AppId = 730, GameName = "Counter-Strike 2", State = DownloadJobState.Queued, AuthorizationConfirmed = true };

        var completed = await manager.StartAsync(job);

        Assert.False(completed);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(0, job.Progress);
        Assert.Contains("not configured", job.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Not configured", job.DownloadMode);
        Assert.False(downloader.WasCalled);
        Assert.Contains("No file was downloaded", job.ProcessLog);
        Assert.Contains(store.Logs, log => log.Message.Contains("no DepotDownloader executable is configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_WithMissingExecutableFailsWithoutRunningAnything()
    {
        var settings = new AppSettings { DepotDownloaderPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe") };
        var (manager, _, downloader) = CreateManager(settings);
        var job = new DownloadJob { AppId = 730, GameName = "Counter-Strike 2", State = DownloadJobState.Queued, AuthorizationConfirmed = true };

        var completed = await manager.StartAsync(job);

        Assert.False(completed);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Contains("not found", job.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(downloader.WasCalled);
    }

    [Fact]
    public async Task VerifyAsync_ReportsWhatWasActuallyFound()
    {
        var (manager, _, _) = CreateManager(new AppSettings());
        var folder = Path.Combine(Path.GetTempPath(), "SteamContentManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "content.bin"), new byte[2048]);
        var job = new DownloadJob { AppId = 730, GameName = "Counter-Strike 2", TargetFolder = folder };

        var passed = await manager.VerifyAsync(job);

        Assert.True(passed);
        Assert.Contains("2 KB", job.Status);
        Assert.Contains("1 file", job.Status);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task VerifyAsync_MissingFolderIsReportedAndNotClaimedAsSuccess()
    {
        var (manager, _, _) = CreateManager(new AppSettings());
        var job = new DownloadJob { AppId = 730, GameName = "Counter-Strike 2", TargetFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };

        var passed = await manager.VerifyAsync(job);

        Assert.False(passed);
        Assert.Contains("does not exist", job.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DownloadJobState.Downloading, false, DownloadJobState.Paused)]
    [InlineData(DownloadJobState.Downloading, true, DownloadJobState.Queued)]
    [InlineData(DownloadJobState.Completed, false, DownloadJobState.Completed)]
    [InlineData(DownloadJobState.Failed, false, DownloadJobState.Failed)]
    [InlineData(DownloadJobState.Queued, false, DownloadJobState.Queued)]
    public void DownloadQueueStore_RestoresInterruptedJobsHonestly(DownloadJobState stored, bool autoResume, DownloadJobState expected)
    {
        var row = new PersistedDownloadJob(
            Guid.NewGuid(), 730, "Counter-Strike 2", "#384F82", "◈", @"D:\Games", 730, "public", string.Empty,
            true, stored.ToString(), 42, "Downloading content", "35.8 GB", "12 GB", DownloadPriority.Normal,
            DateTime.Now.AddHours(-1), DateTime.Now);

        var job = DownloadQueueStore.ToJob(row, autoResume);

        Assert.Equal(expected, job.State);
        Assert.Equal(42, job.Progress);
        Assert.Equal(730, job.DepotId);
        if (stored == DownloadJobState.Downloading)
        {
            Assert.Contains("Interrupted while the app was closed", job.Status);
        }
    }

    [Fact]
    public void DownloadQueueStore_ToRowRoundTripsThroughToJob()
    {
        var original = new DownloadJob
        {
            AppId = 570,
            GameName = "Dota 2",
            TargetFolder = @"E:\Downloads\Dota 2",
            DepotId = 570,
            Branch = "beta",
            ManifestId = "1234567890",
            AuthorizationConfirmed = true,
            TotalSize = "46.2 GB",
            State = DownloadJobState.Paused,
            Status = "Paused by user",
            Priority = DownloadPriority.High
        };
        original.Progress = 33;

        var restored = DownloadQueueStore.ToJob(DownloadQueueStore.ToRow(original), autoResume: false);

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(570, restored.AppId);
        Assert.Equal("Dota 2", restored.GameName);
        Assert.Equal(@"E:\Downloads\Dota 2", restored.TargetFolder);
        Assert.Equal(570, restored.DepotId);
        Assert.Equal("beta", restored.Branch);
        Assert.Equal("1234567890", restored.ManifestId);
        Assert.True(restored.AuthorizationConfirmed);
        Assert.Equal(DownloadJobState.Paused, restored.State);
        Assert.Equal(33, restored.Progress);
        Assert.Equal(DownloadPriority.High, restored.Priority);
    }

    [Fact]
    public async Task DownloadQueueStore_RestoreAsync_FillsJobsOnTheCallingThread()
    {
        var database = new NullLocalDatabase();
        await database.SaveDownloadJobAsync(new PersistedDownloadJob(
            Guid.NewGuid(), 730, "Counter-Strike 2", "#384F82", "◈", @"D:\Games", null, "public", string.Empty,
            true, DownloadJobState.Completed.ToString(), 100, "Completed", "35.8 GB", "35.8 GB", DownloadPriority.Normal,
            DateTime.Now.AddHours(-2), DateTime.Now));

        var store = new AppDataStore();
        var queueStore = new DownloadQueueStore(database, new InMemoryLoggingService(store, database));
        var jobs = new ObservableCollection<DownloadJob>();

        await queueStore.RestoreAsync(jobs, autoResume: false);

        var job = Assert.Single(jobs);
        Assert.Equal(730, job.AppId);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Contains(store.Logs, log => log.Message.Contains("restored", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubSettingsService : ISettingsService
    {
        private AppSettings _settings;

        public StubSettingsService(AppSettings settings) => _settings = settings;

        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { _settings = settings; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken cancellationToken = default) { _settings = new AppSettings(); return Task.CompletedTask; }
    }

    private sealed class StubQueueStore : IDownloadQueueStore
    {
        public List<DownloadJob> Saved { get; } = new();

        public Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default) { Saved.Add(job); return Task.CompletedTask; }
        public Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubDepotDownloader : IDepotDownloaderService
    {
        public bool WasCalled { get; private set; }

        public Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new DepotDownloaderToolStatus(false, executablePath, string.Empty, "Not available in this test", DateTime.Now));

        public Task<DepotDownloaderRunResult> DownloadAsync(DepotDownloaderRequest request, IProgress<DepotDownloaderProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new DepotDownloaderRunResult(0, false, false, "should not run", string.Empty));
        }

        public Task StopAsync(Guid jobId, bool pause, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
