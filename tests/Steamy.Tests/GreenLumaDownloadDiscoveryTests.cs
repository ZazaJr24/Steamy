using Steamy.Services;

namespace Steamy.Tests;

public sealed class GreenLumaDownloadDiscoveryTests
{
    [Fact]
    public void ScanFindsInjectorWithinThreeLevelsButDoesNotRecurseThroughTheWholeDownloadsTree()
    {
        using var folder = new TestFolder();
        var levelThree = Path.Combine(folder.Path, "one", "two", "three");
        var levelFour = Path.Combine(levelThree, "four");
        Directory.CreateDirectory(levelFour);

        File.WriteAllText(Path.Combine(levelThree, "DLLInjector.exe"), "test");
        Assert.Equal(levelThree, GreenLumaDownloadDiscovery.Scan(folder.Path).InjectorDirectory);

        File.Delete(Path.Combine(levelThree, "DLLInjector.exe"));
        File.WriteAllText(Path.Combine(levelFour, "DLLInjector.exe"), "test");
        Assert.Null(GreenLumaDownloadDiscovery.Scan(folder.Path).InjectorDirectory);
    }

    [Fact]
    public void ScanSelectsNewestTopLevelGreenLumaArchiveWhenNoInjectorExists()
    {
        using var folder = new TestFolder();
        var oldArchive = Path.Combine(folder.Path, "GreenLuma-old.zip");
        var newArchive = Path.Combine(folder.Path, "GreenLuma-new.7z");
        File.WriteAllText(oldArchive, "old");
        File.WriteAllText(newArchive, "new");
        File.SetLastWriteTimeUtc(oldArchive, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(newArchive, DateTime.UtcNow);

        Assert.Equal(newArchive, GreenLumaDownloadDiscovery.Scan(folder.Path).ArchivePath);
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Steamy-greenluma-tests-" + Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
