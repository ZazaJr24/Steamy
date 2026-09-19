using SteamContentManager.Models;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class LocalLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SteamContentManagerTests", Guid.NewGuid().ToString("N"));

    private string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void VdfParser_ReadsNestedBlocksValuesAndComments()
    {
        var text = """
            // a comment
            "AppState"
            {
                "appid"      "730"      // inline comment
                "name"       "Counter-Strike 2"
                "nested" { "inner" "value with spaces" }
                "empty"      ""
            }
            /* block
               comment */
            "extra" "after"
            """;

        var root = VdfParser.Parse(text);
        var state = root["AppState"];

        Assert.NotNull(state);
        Assert.Equal(730, state!.GetInt("appid"));
        Assert.Equal("Counter-Strike 2", state.GetString("name"));
        Assert.Equal("value with spaces", state["nested"]!["inner"]!.Value);
        Assert.Equal(string.Empty, state.GetString("empty"));
        Assert.Equal("after", root.GetString("extra"));
    }

    [Fact]
    public void VdfParser_NeverThrowsOnMalformedInput()
    {
        var root = VdfParser.Parse("\"broken\" { \"unterminated\" \"value }  \"second\" \"x\"");

        Assert.NotNull(root);
    }

    [Fact]
    public void ReadApp_ReadsRealAppStateValues()
    {
        var steamApps = CreateDirectory("steamapps");
        var manifest = CreateFile("steamapps/appmanifest_730.acf", """
            "AppState"
            {
                "appid"        "730"
                "name"         "Counter-Strike 2"
                "StateFlags"   "4"
                "installdir"   "Counter-Strike Global Offensive"
                "LastUpdated"  "1726000000"
                "SizeOnDisk"   "38455000000"
                "buildid"      "12345678"
            }
            """);

        var app = SteamLibraryService.ReadApp(manifest, steamApps);

        Assert.NotNull(app);
        Assert.Equal(730, app!.AppId);
        Assert.Equal("Counter-Strike 2", app.Name);
        Assert.Equal(Path.Combine(steamApps, "common", "Counter-Strike Global Offensive"), app.InstallDirectory);
        Assert.Equal(38455000000, app.SizeOnDisk);
        Assert.Equal("12345678", app.BuildId);
        Assert.True(app.IsFullyInstalled);
        Assert.False(app.UpdateRequired);
        Assert.NotNull(app.LastUpdated);
    }

    [Theory]
    [InlineData("4", GameInstallState.Installed)]
    [InlineData("2", GameInstallState.Updating)]
    [InlineData("1026", GameInstallState.Updating)]
    [InlineData("0", GameInstallState.NotInstalled)]
    public void SteamAppStateMapper_MapsStateFlags(string flags, GameInstallState expected)
    {
        Assert.Equal(expected, SteamAppStateMapper.Map(int.Parse(flags)));
    }

    [Fact]
    public void ReadApps_SkipsUnreadableManifestsAndKeepsValidOnes()
    {
        var steamApps = CreateDirectory("steamapps");
        CreateFile("steamapps/appmanifest_730.acf", "\"AppState\" { \"appid\" \"730\" \"name\" \"CS2\" \"StateFlags\" \"4\" }");
        CreateFile("steamapps/appmanifest_broken.acf", "\"AppState\" { \"appid\" \"nonsense\" }");

        var apps = SteamLibraryService.ReadApps(steamApps).ToArray();

        Assert.Single(apps);
        Assert.Equal(730, apps[0].AppId);
    }

    [Fact]
    public void ReadLibraryFolders_ReturnsExistingFoldersOnly()
    {
        var steamApps = CreateDirectory("steamapps");
        var extraLibrary = CreateDirectory("library-b");
        CreateDirectory("library-b", "steamapps");
        var missingLibrary = Path.Combine(_root, "library-missing");

        CreateFile("steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"  "{{Escape(Path.Combine(_root, "library-a"))}}"
                }
                "1"
                {
                    "path"  "{{Escape(extraLibrary)}}"
                }
                "2"
                {
                    "path"  "{{Escape(missingLibrary)}}"
                }
            }
            """);
        CreateDirectory("library-a", "steamapps");

        var folders = SteamLibraryService.ReadLibraryFolders(steamApps);

        Assert.Contains(steamApps, folders);
        Assert.Contains(Path.Combine(_root, "library-a", "steamapps"), folders);
        Assert.Contains(Path.Combine(extraLibrary, "steamapps"), folders);
        Assert.DoesNotContain(Path.Combine(missingLibrary, "steamapps"), folders);
    }

    [Fact]
    public void ReadLibraryFolders_SupportsTheOldFlatFormat()
    {
        var steamApps = CreateDirectory("steamapps");
        var oldLibrary = CreateDirectory("old-library");
        CreateDirectory("old-library", "steamapps");
        CreateFile("steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "1"  "{{Escape(oldLibrary)}}"
            }
            """);

        var folders = SteamLibraryService.ReadLibraryFolders(steamApps);

        Assert.Contains(Path.Combine(oldLibrary, "steamapps"), folders);
    }

    [Fact]
    public void Scan_ReturnsAFailureWithoutInventingEntriesWhenNoSteamExists()
    {
        var result = new SteamLibraryService().Scan(Path.Combine(_root, "not-installed"));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Apps);
        Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ReadsAppsFromAProvidedSteamFolder()
    {
        var steamApps = CreateDirectory("steamapps");
        CreateFile("steamapps/appmanifest_570.acf", "\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"StateFlags\" \"4\" \"installdir\" \"dota 2 beta\" \"SizeOnDisk\" \"1000000\" }");
        CreateFile("steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"0\" { \"path\" \"" + Escape(_root) + "\" } }");

        var result = new SteamLibraryService().Scan(_root);

        Assert.True(result.Succeeded);
        var app = Assert.Single(result.Apps);
        Assert.Equal(570, app.AppId);
        Assert.Equal("Dota 2", app.Name);
        Assert.Contains(steamApps, result.LibraryFolders);
    }

    [Fact]
    public void GameFactory_FormatsRealInstallData()
    {
        var app = new InstalledSteamApp
        {
            AppId = 1172470,
            Name = "Apex Legends",
            InstallDirectory = @"D:\Steam\steamapps\common\Apex Legends",
            SizeOnDisk = 72_400_000_000,
            StateFlags = 4,
            BuildId = "42",
            LastUpdated = new DateTime(2026, 2, 19, 10, 30, 0)
        };

        var game = GameFactory.FromInstalledApp(app);

        Assert.Equal(1172470, game.AppId);
        Assert.Equal("Apex Legends", game.Name);
        Assert.Equal("AL", game.ShortName);
        Assert.Equal(GameInstallState.Installed, game.InstallState);
        Assert.Equal(@"D:\Steam\steamapps\common\Apex Legends", game.InstallFolder);
        Assert.Contains("19.02.2026", game.LastPlayed);
        Assert.Contains("build 42", game.DepotSummary);
        Assert.Equal(72_400_000_000, game.SizeOnDiskBytes);
        Assert.False(game.Selected);
    }

    [Theory]
    [InlineData(0, "Unknown size")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void ByteSize_FormatsRealByteCounts(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void GameFactory_CoverStyleIsStableForTheSameAppId()
    {
        var first = GameFactory.CoverStyleFor(730);
        var second = GameFactory.CoverStyleFor(730);

        Assert.Equal(first, second);
        Assert.NotEqual(first, GameFactory.CoverStyleFor(731));
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
