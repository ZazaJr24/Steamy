using System.Text.Json;
using Steamy.Storage;

namespace Steamy.Tests;

public sealed class DataLocationTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "Steamy-storage-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) => Path.Combine(_area, name);
    [Fact]
    public void LegacyDataIsCopiedToRoamingOnceWithoutReplacingNewerData()
    {
        var roaming = Folder("roaming"); var local = Folder("local");
        Directory.CreateDirectory(Path.Combine(local, "games")); Directory.CreateDirectory(roaming);
        File.WriteAllText(Path.Combine(local, "games", "1.json"), "notes");
        File.WriteAllText(Path.Combine(local, "playtime.json"), "old time");
        File.WriteAllText(Path.Combine(roaming, "playtime.json"), "current time");
        var manager = new StorageLocationManager(roaming);
        Assert.Equal(roaming, manager.Apply(local));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(roaming, "games", "1.json")));
        Assert.Equal("current time", File.ReadAllText(Path.Combine(roaming, "playtime.json")));
        Assert.True(File.Exists(Path.Combine(local, "games", "1.json")));
        File.WriteAllText(Path.Combine(local, "games", "2.json"), "stale source");
        manager.Apply(local); Assert.False(File.Exists(Path.Combine(roaming, "games", "2.json")));
    }
    [Fact]
    public void SelectedFolderIsAppliedOnlyAfterCopyWithCredentialsAndPathPreferencesPreserved()
    {
        var roaming = Folder("roaming"); var target = Folder("custom");
        var manager = new StorageLocationManager(roaming); manager.Apply(Folder("absent"));
        Directory.CreateDirectory(Path.Combine(roaming, "credentials"));
        var secret = new byte[] { 0, 99, 200, 3 }; File.WriteAllBytes(Path.Combine(roaming, "credentials", "key"), secret);
        File.WriteAllText(Path.Combine(roaming, "settings.json"), JsonSerializer.Serialize(new { SteamlessPath = Path.Combine(roaming, "tools", "tool.exe"), DownloadFolder = Folder("games"), RyuuApiKey = "same-value" }));
        manager.Schedule(target);
        Assert.Equal(roaming, manager.Read().ActiveRoot); Assert.False(File.Exists(Path.Combine(target, "settings.json")));
        Assert.Equal(target, manager.Apply(Folder("absent")));
        Assert.Equal(secret, File.ReadAllBytes(Path.Combine(target, "credentials", "key")));
        using var prefs = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal(Path.Combine(target, "tools", "tool.exe"), prefs.RootElement.GetProperty("SteamlessPath").GetString());
        Assert.Equal(Folder("games"), prefs.RootElement.GetProperty("DownloadFolder").GetString());
        Assert.Equal("same-value", prefs.RootElement.GetProperty("RyuuApiKey").GetString());
        Assert.True(File.Exists(Path.Combine(roaming, "settings.json"))); Assert.Null(manager.Read().PendingRoot);
    }
    [Fact]
    public void UnsafeDestinationsAndLateFilesNeverOverwriteOrSwitchTheActiveRoot()
    {
        var roaming = Folder("roaming"); var target = Folder("custom");
        var manager = new StorageLocationManager(roaming); manager.Apply(Folder("absent"));
        Assert.Throws<IOException>(() => manager.Schedule(Path.Combine(roaming, "nested")));
        Assert.Throws<IOException>(() => manager.Schedule(_area));
        manager.Schedule(target); File.WriteAllText(Path.Combine(target, "unrelated.txt"), "keep");
        Assert.Throws<IOException>(() => manager.Apply(Folder("absent")));
        Assert.Equal(roaming, manager.Read().ActiveRoot); Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        manager.Cancel(); Assert.Null(manager.Read().PendingRoot);
    }
    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
