using Steamy.Services;

namespace Steamy.Core.Tests;

public sealed class SteamPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Steamy-platform-tests-" + Guid.NewGuid().ToString("N"));
    public SteamPlatformTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void FindsNativeAndFlatpakIndependently()
    {
        var native = Path.Combine(_root, ".local", "share", "Steam");
        var flatpak = Path.Combine(_root, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(native, "steamapps"));
        Directory.CreateDirectory(Path.Combine(flatpak, "steamapps"));
        var installations = SteamInstallationLocator.FindAll(_root, SteamPlatform.Linux);
        Assert.Equal(2, installations.Count);
        Assert.Contains(installations, i => i.Root == native && i.Distribution == SteamDistribution.Native);
        Assert.Contains(installations, i => i.Root == flatpak && i.Distribution == SteamDistribution.Flatpak);
    }

    [Fact]
    public void HonoursXdgAndMacRootsWithoutWindowsRegistryAccess()
    {
        var xdg = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(xdg, "Steam", "steamapps"));
        Directory.CreateDirectory(Path.Combine(_root, "Library", "Application Support", "Steam", "steamapps"));
        Assert.Single(SteamInstallationLocator.FindAll(_root, SteamPlatform.Linux, xdg));
        Assert.Single(SteamInstallationLocator.FindAll(_root, SteamPlatform.MacOS));
        Assert.Contains(Path.Combine(xdg, "Steam"), SteamInstallationLocator.GetCandidates(_root, SteamPlatform.Linux, xdg).Select(i => i.Root));
    }

    [Fact]
    public void SharedReaderReadsSecondaryDriveAndActualDownloadState()
    {
        var primary = Path.Combine(_root, "Steam", "steamapps");
        var secondaryRoot = Path.Combine(_root, "second drive");
        var secondary = Path.Combine(secondaryRoot, "steamapps");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(secondary);
        File.WriteAllText(Path.Combine(primary, "libraryfolders.vdf"),
            "\"libraryfolders\" { \"1\" { \"path\" \"" + secondaryRoot.Replace("\\", "\\\\") + "\" } }");
        File.WriteAllText(Path.Combine(secondary, "appmanifest_10.acf"),
            "\"AppState\" { \"appid\" \"10\" \"name\" \"Counter-Strike\" \"installdir\" \"Half-Life\" \"StateFlags\" \"1024\" \"BytesDownloaded\" \"125\" \"BytesToDownload\" \"500\" }");
        File.WriteAllText(Path.Combine(primary, "appmanifest_bad.acf"), "broken manifest");
        var result = new SteamLibraryService().Scan(Path.GetDirectoryName(primary));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.LibraryFolders.Count);
        var app = Assert.Single(result.Apps);
        Assert.Equal(10, app.AppId);
        Assert.Equal(25d, app.DownloadProgressPercent);
        Assert.True(app.UpdateInProgress);
        Assert.Equal(Path.Combine(secondary, "common", "Half-Life"), app.InstallDirectory);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("..")]
    public void ManifestCannotSupplyAnInstallFolderOutsideCommon(string directory)
    {
        var manifest = Path.Combine(_root, "appmanifest_10.acf");
        File.WriteAllText(manifest, "\"AppState\" { \"appid\" \"10\" \"installdir\" \"" + directory + "\" }");
        Assert.Null(SteamLibraryService.ReadApp(manifest, _root));
    }

    [Fact]
    public void MissingProgressIsUnknownAndNotSimulated()
    {
        Assert.Null(new InstalledSteamApp { AppId = 10 }.DownloadProgressPercent);
        Assert.Equal(100d, new InstalledSteamApp { BytesDownloaded = long.MaxValue, BytesToDownload = 1 }.DownloadProgressPercent);
    }

    [Theory]
    [InlineData(SteamPlatform.Windows, SteamDistribution.Native, "steam.exe")]
    [InlineData(SteamPlatform.Linux, SteamDistribution.Native, "steam")]
    [InlineData(SteamPlatform.Linux, SteamDistribution.Flatpak, "flatpak")]
    [InlineData(SteamPlatform.MacOS, SteamDistribution.Native, "open")]
    public void CommandsUseArgumentListsForTheSelectedClient(SteamPlatform platform, SteamDistribution distribution, string executable)
    {
        var start = SteamActions.BuildStartInfo(new(Path.Combine(_root, "Steam path"), platform, distribution), SteamAction.Install, 10);
        Assert.Equal(executable, Path.GetFileName(start.FileName));
        Assert.False(start.UseShellExecute);
        Assert.Contains("steam://install/10", start.ArgumentList);
        if (distribution == SteamDistribution.Flatpak) Assert.Equal(["run", "com.valvesoftware.Steam", "steam://install/10"], start.ArgumentList);
    }

    [Fact]
    public void InvalidSteamActionsDoNotLaunchAnything()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamActions.GetUri(SteamAction.Install, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamActions.GetUri(SteamAction.Run, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamActions.GetUri((SteamAction)100));
    }

    [Fact]
    public void PlatformStorageNeverDependsOnWorkingDirectory()
    {
        Assert.Equal(Path.Combine(_root, ".local", "share", "Steamy"), PlatformPaths.GetDataRoot(_root, SteamPlatform.Linux));
        Assert.Equal(Path.Combine(_root, "Library", "Application Support", "Steamy"), PlatformPaths.GetDataRoot(_root, SteamPlatform.MacOS));
        Assert.Equal(Path.Combine(_root, "data", "Steamy"), PlatformPaths.GetDataRoot(_root, SteamPlatform.Linux, Path.Combine(_root, "data")));
        Assert.Equal(_root, PlatformPaths.GetDataRoot("ignored", SteamPlatform.Linux, dataOverride: _root));
    }
}
