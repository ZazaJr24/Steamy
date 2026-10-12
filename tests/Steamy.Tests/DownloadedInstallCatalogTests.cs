using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadedInstallCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Steamy-installs-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }

    [Fact]
    public async Task CompletedDownloadsSurviveQueueRemovalButDisappearAfterUninstall()
    {
        var game = Folder("A game");
        await File.WriteAllTextAsync(Path.Combine(game, "game.bin"), "downloaded content");
        var index = new DownloadedInstallCatalog(Path.Combine(_root, "index.json"));
        Assert.Single(await index.DiscoverAsync([new(42, "Game", game)], []));
        var afterQueueCleared = await new DownloadedInstallCatalog(Path.Combine(_root, "index.json")).DiscoverAsync([], []);
        Assert.Equal(42, Assert.Single(afterQueueCleared).AppId);
        Directory.Delete(game, true);
        Assert.Empty(await index.DiscoverAsync([], []));
    }

    [Fact]
    public async Task IdentityAndExecutableDiscoverUnregisteredGamesWithoutGuessingNumericDepotIds()
    {
        var game = Folder("External game");
        await File.WriteAllTextAsync(Path.Combine(game, "steam_appid.txt"), "42");
        await File.WriteAllBytesAsync(Path.Combine(game, "game.exe"), new byte[150_000]);
        var unknown = Folder("12345");
        await File.WriteAllBytesAsync(Path.Combine(unknown, "game.exe"), new byte[150_000]);
        var metadataOnly = Folder("Partial");
        await File.WriteAllTextAsync(Path.Combine(metadataOnly, "steam_appid.txt"), "55");
        var index = new DownloadedInstallCatalog(Path.Combine(_root, "index.json"));
        var discovered = await index.DiscoverAsync([], [_root]);
        var found = Assert.Single(discovered);
        Assert.Equal(42, found.AppId);
        Assert.Equal(game, found.Folder);
    }

    [Fact]
    public async Task EmptyFoldersAndDownloaderMetadataDoNotBecomeInstalledGames()
    {
        var empty = Folder("Empty");
        var partial = Folder("Partial");
        Directory.CreateDirectory(Path.Combine(partial, ".DepotDownloader"));
        await File.WriteAllTextAsync(Path.Combine(partial, ".DepotDownloader", "chunk.dat"), "unfinished");
        var index = new DownloadedInstallCatalog(Path.Combine(_root, "index.json"));
        Assert.Empty(await index.DiscoverAsync([new(42, "Empty", empty), new(43, "Partial", partial)], []));
    }

    [Fact]
    public async Task CompletedGameNameWinsOverNestedSteamIdMarkers()
    {
        var game = Folder("My game");
        var binaries = Path.Combine(game, "Binaries", "Win64");
        Directory.CreateDirectory(binaries);
        await File.WriteAllTextAsync(Path.Combine(binaries, "steam_appid.txt"), "42");
        await File.WriteAllBytesAsync(Path.Combine(binaries, "game.exe"), new byte[150_000]);
        var index = new DownloadedInstallCatalog(Path.Combine(_root, "index.json"));
        var found = Assert.Single(await index.DiscoverAsync([new(42, "Official title", game)], [_root]));
        Assert.Equal("Official title", found.Name);
        Assert.Equal(game, found.Folder);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task ASteamIdentityReplacesTheSameFoldersStandaloneIdentity()
    {
        var folder = Folder("Standalone");
        await File.WriteAllBytesAsync(Path.Combine(folder, "Game.exe"), new byte[150_000]);
        var index = new DownloadedInstallCatalog(Path.Combine(_root, "index.json"));
        Assert.Equal(-42, Assert.Single(await index.DiscoverAsync([new(-42, "Local game", folder, "Local")], [])).AppId);
        var imported = Assert.Single(await index.DiscoverAsync([new(42, "Steam title", folder)], []));
        Assert.Equal(42, imported.AppId);
        Assert.Equal("Steam title", imported.Name);
    }
}
