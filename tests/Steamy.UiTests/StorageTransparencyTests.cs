using System.IO;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class StorageTransparencyTests
{
    [Fact]
    public void StorageMapAndReportDoNotCreateFoldersOrExposeCredentials()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Steamy-transparency-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { DownloadFolder = missing, RyuuApiKey = "secret-ryuu-value", HubcapApiKey = "secret-hubcap-value", SteamUsername = "private-account-name" };
        var groups = StorageTransparency.Describe(settings, [], []);
        var report = StorageTransparency.Report(groups);
        Assert.False(Directory.Exists(missing));
        Assert.Contains(missing, report);
        Assert.DoesNotContain("secret-ryuu-value", report); Assert.DoesNotContain("secret-hubcap-value", report); Assert.DoesNotContain("private-account-name", report);
        var entry = groups.SelectMany(group => group.Locations).Single(item => item.Name == "Configured download folder");
        Assert.False(entry.Exists); Assert.Contains("Not present", entry.Status);
        Assert.Contains(groups.SelectMany(group => group.Locations), item => item.Name == "Per-game notes and startup options" && item.Path == Path.Combine(CacheTempService.Root, "games"));
        Assert.All(groups.SelectMany(group => group.Locations), item => { Assert.NotEmpty(item.Purpose); Assert.NotEmpty(item.CreatedWhen); Assert.NotEmpty(item.RemovalImpact); });
    }
    [Fact]
    public void JobTargetsDumpPreferenceAndSelectedToolFilesAreIncludedWithoutScanning()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-read-only-map-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "downloads"), ManifestDumpFolder = Path.Combine(root, "dumps"), SteamLibraryPath = Path.Combine(root, "Steam"), SteamlessTargetExePath = Path.Combine(root, "game.exe") };
        var groups = StorageTransparency.Describe(settings,
            [new DownloadJob { GameName = "Depot game", TargetFolder = Path.Combine(root, "depot") }],
            [new Game { Name = "Local game", IsLocalDownload = true, InstallFolder = Path.Combine(root, "local") }]);
        var paths = groups.SelectMany(group => group.Locations).Select(item => item.Path).ToArray();
        Assert.Contains(Path.Combine(root, "dumps", "DepotDumps"), paths);
        Assert.Contains(Path.Combine(root, "depot"), paths); Assert.Contains(Path.Combine(root, "local"), paths); Assert.Contains(Path.Combine(root, "game.exe"), paths);
        Assert.False(Directory.Exists(root));
    }
}
