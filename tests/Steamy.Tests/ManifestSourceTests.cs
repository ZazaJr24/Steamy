using System.IO.Compression;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class ManifestSourceTests
{
    [Fact]
    public void SushiIndexReadsEveryRootArchiveAndRejectsAmbiguousOrUnrelatedNames()
    {
        var ids = GitHubManifestIndex.Read("""
            {"truncated":false,"tree":[
              {"path":"10.zip","type":"blob"},{"path":"400.ZIP","type":"blob"},
              {"path":"001.zip","type":"blob"},{"path":"0.zip","type":"blob"},
              {"path":"nested/500.zip","type":"blob"},{"path":"README.md","type":"blob"},
              {"path":"900.zip","type":"tree"},{"path":"999999999999.zip","type":"blob"}]}
            """, true);
        Assert.Equal(new[] { 10, 400 }, ids.Order().ToArray());
    }

    [Fact]
    public void ZazaIndexUsesAppDirectoriesAndNeverTreatsAManifestDepotAsAnApp()
    {
        var ids = GitHubManifestIndex.Read("""
            {"tree":[{"path":"440","type":"tree"},{"path":"441_1234.manifest","type":"blob"},{"path":"440.lua","type":"blob"}]}
            """, false);
        Assert.Equal(new[] { 440 }, ids.ToArray());
    }

    [Fact]
    public void TruncatedIndexCannotClaimMissingGamesAreUnavailable() => Assert.Throws<InvalidDataException>(() => GitHubManifestIndex.Read("""{"truncated":true,"tree":[]} """, true));

    [Theory]
    [InlineData("DepotDownloaderMod (Sushi)", "Sushi", true)]
    [InlineData("DepotDownloaderMod (Zaza)", "Sushi", false)]
    [InlineData("DepotDownloaderMod (Sushi)", "DepotDownloader", false)]
    [InlineData("DepotDownloader", "DepotDownloader", true)]
    [InlineData("DepotDownloaderMod (Local)", "Local", true)]
    [InlineData("Custom archive", "Custom archive", true)]
    [InlineData("DepotDownloaderMod (Hubcap)", "All sources", true)]
    public void DownloadSourceFilterRetainsTheActualToolAndProvider(string mode, string source, bool matches) => Assert.Equal(matches, GitHubManifestIndex.Matches(mode, source));

    [Fact]
    public async Task ExtractsMetadataWithoutExecutablesOrTraversingDirectories()
    {
        using var folder = new TestFolder();
        var archive = CreateArchive(folder.Path, ("pack/10.lua", "addappid(10)"), ("pack/1_123.manifest", "manifest"), ("tool.exe", "executable"));
        var destination = System.IO.Path.Combine(folder.Path, "output");
        var result = await ManifestArchiveReader.ExtractAsync(archive, destination);
        Assert.Equal("addappid(10)", result.Lua);
        Assert.Equal(2, result.Files);
        Assert.Equal(2, Directory.GetFiles(destination).Length);
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "tool.exe")));
    }

    [Theory]
    [InlineData("../10.lua", "other.lua")]
    [InlineData("/10.lua", "other.lua")]
    [InlineData("a/10.lua", "b/10.lua")]
    [InlineData("a/10.lua", "b/10.LUA")]
    [InlineData("C:\\10.lua", "other.lua")]
    public async Task UnsafeOrDuplicateMetadataFailsBeforeWritingAnyFile(string first, string second)
    {
        using var folder = new TestFolder();
        var archive = CreateArchive(folder.Path, (first, "first"), (second, "second"));
        var destination = System.IO.Path.Combine(folder.Path, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => ManifestArchiveReader.ExtractAsync(archive, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CancelledArchiveDoesNotStartExtraction()
    {
        using var folder = new TestFolder();
        var archive = CreateArchive(folder.Path, ("10.lua", "metadata"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var destination = System.IO.Path.Combine(folder.Path, "output");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManifestArchiveReader.ExtractAsync(archive, destination, cancellation.Token));
        Assert.False(Directory.Exists(destination));
    }

    private static string CreateArchive(string directory, params (string Name, string Content)[] files)
    {
        var path = System.IO.Path.Combine(directory, "source.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open());
            writer.Write(file.Content);
        }
        return path;
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
