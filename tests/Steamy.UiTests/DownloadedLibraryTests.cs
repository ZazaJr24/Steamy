using System.IO;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DownloadedLibraryTests
{
    [Fact]
    public async Task MergesSteamWithExternalDepotAndStandaloneGamesBeyondThreeFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-mixed-" + Guid.NewGuid().ToString("N"));
        var depot = Path.Combine(root, "Downloads", "a", "b", "c", "d", "External depot game");
        var local = Path.Combine(root, "Downloads", "Standalone");
        Directory.CreateDirectory(depot);
        Directory.CreateDirectory(local);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(depot, "DepotGame.exe"), new byte[150_000]);
            await File.WriteAllTextAsync(Path.Combine(depot, "steam_emu.ini"), "[Settings]\nAppId=42\n");
            await File.WriteAllBytesAsync(Path.Combine(local, "Standalone.exe"), new byte[150_000]);
            await File.WriteAllTextAsync(Path.Combine(local, "Standalone.pck"), "game data");
            var steam = new SteamFixture { Result = new(true, "Steam fixture", root, [root],
                [new InstalledSteamApp { AppId = 99, Name = "Steam game", InstallDirectory = "Steam game", StateFlags = 4 }]) };
            var store = new AppDataStore();
            var database = new NullLocalDatabase();
            var settings = new SettingsFixture(Path.Combine(root, "Downloads"));
            var logging = (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService));
            var downloads = new DownloadedGameLibrary(database, settings, new GameLocatorService(steam, store, database), logging, Path.Combine(root, "installs.json"));
            var sync = new LibrarySyncService(store, steam, new ArtworkFixture(), logging, settings, downloads);
            await sync.RefreshAsync();
            Assert.Equal(3, store.Games.Count);
            Assert.Contains(store.Games, game => game.AppId == 99 && !game.IsLocalDownload);
            Assert.Contains(store.Games, game => game.AppId == 42 && game.InstallationSource == "DepotDownloader" && game.InstallFolder == depot);
            Assert.Contains(store.Games, game => game.AppId < 0 && game.InstallationSource == "Local" && game.InstallFolder == local);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SavedXboxContentInstallationRecoversItsTitleAndDetectedExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-xbox-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Herdling", "Content");
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "Herdling.exe");
            await File.WriteAllBytesAsync(exe, new byte[150_000]);
            var library = new DownloadedGameLibrary(new NullLocalDatabase(), new SettingsFixture(root),
                (IGameLocatorService)OfflineServiceProxy.Create(typeof(IGameLocatorService)),
                (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService)), Path.Combine(root, "installs.json"));
            await library.ImportAsync([new(-42, "Content", folder, "Local", exe)]);
            var game = Assert.Single(await library.ScanAsync());
            Assert.Equal("Herdling", game.Name);
            Assert.Equal(exe, game.LaunchExecutable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DownloadsMergeWithSteamSurviveClearedHistoryAndWorkWithoutSteam()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "Beach game");
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, "BeachGame.exe");
            await File.WriteAllBytesAsync(exe, new byte[150_000]);
            var database = new NullLocalDatabase();
            await database.SaveDownloadJobAsync(DownloadQueueStore.ToRow(new DownloadJob
                { AppId = 42, GameName = "Beach game", TargetFolder = folder, State = DownloadJobState.Completed }));
            var paused = Path.Combine(root, "Paused");
            Directory.CreateDirectory(paused);
            await File.WriteAllBytesAsync(Path.Combine(paused, "OtherGame.exe"), new byte[150_000]);
            await database.SaveDownloadJobAsync(DownloadQueueStore.ToRow(new DownloadJob
                { AppId = 43, GameName = "Paused", TargetFolder = paused, State = DownloadJobState.Paused }));
            var store = new AppDataStore();
            var steam = new SteamFixture();
            var settings = new SettingsFixture(root);
            var logging = (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService));
            var downloaded = new DownloadedGameLibrary(database, settings,
                new GameLocatorService(steam, store, database), logging, Path.Combine(root, "installs.json"));
            var sync = new LibrarySyncService(store, steam, new ArtworkFixture(), logging, settings, downloaded);
            await sync.RefreshAsync();
            var game = Assert.Single(store.Games);
            Assert.Equal(42, game.AppId);
            Assert.True(game.IsLocalDownload);
            Assert.Equal(exe, game.LaunchExecutable);
            await database.ClearAllDownloadJobsAsync();
            await sync.RefreshAsync();
            Assert.Equal(42, Assert.Single(store.Games).AppId);
            steam.Result = new(true, "Steam fixture", root, [root],
                [new InstalledSteamApp { AppId = 42, Name = "Steam copy", InstallDirectory = folder, StateFlags = 4 }]);
            await sync.RefreshAsync();
            Assert.False(Assert.Single(store.Games).IsLocalDownload); // One card per app, Steam launch takes precedence.
            steam.Result = new(true, "No Steam installations", root, [root], []);
            await sync.RefreshAsync();
            Assert.True(Assert.Single(store.Games).IsLocalDownload);
            steam.Result = SteamLibraryScanResult.Failure("Steam unavailable");
            await sync.RefreshAsync();
            Assert.Equal(42, Assert.Single(store.Games).AppId);
            Directory.Delete(folder, true);
            await sync.RefreshAsync();
            Assert.Empty(store.Games);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SteamFixture : ISteamLibraryService
    {
        public SteamLibraryScanResult Result { get; set; } = SteamLibraryScanResult.Failure("Steam unavailable");
        public SteamLibraryScanResult Scan(string? steamRootOverride = null) => Result;
    }

    private sealed class SettingsFixture(string root) : ISettingsService
    {
        public AppSettings Load() => new() { DownloadFolder = root };
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ArtworkFixture : IArtworkService
    {
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
