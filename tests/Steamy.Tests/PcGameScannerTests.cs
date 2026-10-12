using Steamy.Services;

namespace Steamy.Tests;

public sealed class PcGameScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Steamy-pc-scan-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { var path = Path.GetFullPath(Path.Combine(_root, name)); Directory.CreateDirectory(path); return path; }
    private static void Exe(string folder, string name = "Game.exe") => File.WriteAllBytes(Path.Combine(folder, name), new byte[150_000]);

    [Fact]
    public void ScansBeyondTheOldDepthLimitAndReadsSteamEmulatorIdentity()
    {
        var folder = Folder("a/b/c/d/e/f/Deep game");
        Exe(folder);
        File.WriteAllText(Path.Combine(folder, "steam_emu.ini"), "[Settings]\nAppId=42\n");
        var result = PcGameScanner.Scan([_root]);
        var game = Assert.Single(result.Games);
        Assert.Equal(42, game.AppId);
        Assert.Equal(folder, game.Folder);
        Assert.False(result.Cancelled);
        Assert.True(result.Folders > 4);
    }

    [Fact]
    public void DiscoversAnUnregisteredSteamLibraryAndRejectsOrdinaryApplications()
    {
        var steamapps = Folder("Other library/steamapps");
        var gameFolder = Folder("Other library/steamapps/common/My game");
        Exe(gameFolder);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_42.acf"), "\"AppState\" { \"appid\" \"42\" \"name\" \"My game\" \"installdir\" \"My game\" \"StateFlags\" \"4\" }");
        Exe(Folder("Ordinary editor"), "Editor.exe");
        var game = Assert.Single(PcGameScanner.Scan([_root]).Games);
        Assert.Equal("Steam", game.Source);
        Assert.Equal(gameFolder, game.Folder);
    }

    [Fact]
    public void RecognizesStandaloneEngineDataWithoutInventingASteamId()
    {
        var folder = Folder("Standalone");
        Exe(folder);
        File.WriteAllText(Path.Combine(folder, "Game.pck"), "engine data");
        var game = Assert.Single(PcGameScanner.Scan([_root]).Games);
        Assert.True(game.AppId < 0);
        Assert.Equal("Local", game.Source);
        Assert.Equal(PcGameScanner.LocalId(folder), game.AppId);
        Assert.Equal(game.AppId, Assert.Single(PcGameScanner.Scan([_root]).Games).AppId);
    }

    [Fact]
    public void XboxContentFolderUsesTheGameTitleAndKeepsItsExecutable()
    {
        var folder = Folder("Xbox games/Herdling/Content");
        Exe(folder, "Herdling.exe");
        File.WriteAllText(Path.Combine(folder, "UnityPlayer.dll"), "engine");
        Directory.CreateDirectory(Path.Combine(folder, "Herdling_Data"));
        var game = Assert.Single(PcGameScanner.Scan([_root]).Games);
        Assert.Equal("Herdling", game.Name);
        Assert.Equal(Path.Combine(folder, "Herdling.exe"), game.Executable);
        Assert.Equal("Among Us", PcGameScanner.ResolveName("Content", Path.Combine(_root, "Among Us", "Content"), ""));
        Assert.Equal("Content Warning", PcGameScanner.ResolveName("Content Warning", folder, ""));
    }

    [Fact]
    public void CancellationKeepsAlreadyFoundGames()
    {
        var folder = Folder("Game");
        Exe(folder);
        File.WriteAllText(Path.Combine(folder, "steam_appid.txt"), "42");
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(p => { if (p.Games > 0) cancellation.Cancel(); });
        var another = Folder("Another");
        var result = PcGameScanner.Scan([another, folder], progress, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(42, Assert.Single(result.Games).AppId);
    }

    [Fact]
    public void ReadsEpicAndGogInstallationsAndDeduplicatesNestedIdentity()
    {
        var epic = Folder("Epic game");
        Exe(epic);
        var manifests = Folder("Manifests");
        File.WriteAllText(Path.Combine(manifests, "game.item"), System.Text.Json.JsonSerializer.Serialize(new { InstallLocation = epic, DisplayName = "Epic title" }));
        var gog = Folder("GOG game");
        Exe(gog);
        File.WriteAllText(Path.Combine(gog, "goggame-123.info"), "{\"name\":\"GOG title\"}");
        var steam = Folder("Steam game");
        var binaries = Folder("Steam game/Binaries/Win64");
        Exe(binaries);
        File.WriteAllText(Path.Combine(steam, "steam_appid.txt"), "42");
        File.WriteAllText(Path.Combine(binaries, "steam_appid.txt"), "42");
        var games = PcGameScanner.Scan([_root]).Games;
        Assert.Equal(3, games.Count);
        Assert.Contains(games, g => g.Source == "Epic" && g.Name == "Epic title");
        Assert.Contains(games, g => g.Source == "GOG" && g.Name == "GOG title");
        Assert.Equal(steam, Assert.Single(games, g => g.AppId == 42).Folder);
    }

    private sealed class SynchronousProgress(Action<GameScanProgress> report) : IProgress<GameScanProgress>
    { public void Report(GameScanProgress value) => report(value); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
