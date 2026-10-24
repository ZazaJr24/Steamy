using System.IO;
using System.IO.Compression;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class LocalGameCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Steamy-catalog-test-" + Guid.NewGuid().ToString("N"));
    public LocalGameCatalogTests() => Directory.CreateDirectory(_folder);
    private LocalGameCatalog Catalog => new(Path.Combine(_folder, "data", "catalog.json"));
    private string MakeArchive(string filename = "Example.Game.Build.12345-DenuvOwO.zip")
    {
        var path = Path.Combine(_folder, filename);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("steam_settings/steam_appid.txt").Open())) writer.Write("123456");
        using (var writer = new StreamWriter(zip.CreateEntry("Readme.txt").Open())) writer.Write("Package-specific instructions.\n2026\nKeep original game files.");
        using (var writer = new StreamWriter(zip.CreateEntry("never-run.cmd").Open())) writer.Write("exit 99");
        return path;
    }
    [Fact]
    public async Task ImportReadsMetadataKeepsArchivesAndSkipsDuplicates()
    {
        var archive = MakeArchive();
        var original = await File.ReadAllBytesAsync(archive);
        Assert.Equal(1, await Catalog.ImportFolderAsync(_folder, "Fix"));
        Assert.Equal(0, await Catalog.ImportFolderAsync(_folder, "Fix"));
        var package = Assert.Single(await Catalog.ReadAsync());
        Assert.Equal("Example Game", package.Name); Assert.Equal("Build 12345", package.Version);
        Assert.Equal("123456", package.AppId); Assert.Contains("Package-specific instructions", package.Notes);
        Assert.Equal(original, await File.ReadAllBytesAsync(archive));
        Assert.False(File.Exists(Path.Combine(_folder, "never-run.cmd")));
    }
    [Fact]
    public async Task LocalFixesExposeLocalPathsAndInstructionsAndRemovalKeepsArchive()
    {
        var archive = MakeArchive();
        await Catalog.ImportFolderAsync(_folder, "Fix");
        var game = Assert.Single(await Catalog.ReadFixesAsync());
        var fix = Assert.Single(game.Fixes);
        Assert.Equal(archive, fix.LocalArchivePath); Assert.Contains("Package-specific instructions", fix.Instructions);
        await Catalog.RemoveAsync(Assert.Single(await Catalog.ReadAsync()).Id);
        Assert.Empty(await Catalog.ReadAsync()); Assert.True(File.Exists(archive));
        Assert.True(File.Exists(Catalog.CatalogPath + ".bak"));
    }
    [Fact]
    public async Task ConcurrentEditorsDoNotLoseEachOthersEntries()
    {
        var archive = MakeArchive();
        var first = new LocalGamePackage { Name = "First", ArchivePath = archive };
        var second = new LocalGamePackage { Name = "Second", ArchivePath = archive };
        await Task.WhenAll(Catalog.UpsertAsync(first), Catalog.UpsertAsync(second));
        Assert.Equal(2, (await Catalog.ReadAsync()).Count);
    }
    [Fact]
    public async Task InvalidExistingCatalogIsNotOverwrittenByAnImport()
    {
        MakeArchive(); Directory.CreateDirectory(Path.GetDirectoryName(Catalog.CatalogPath)!);
        await File.WriteAllTextAsync(Catalog.CatalogPath, "broken catalog");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Catalog.ImportFolderAsync(_folder, "Fix"));
        Assert.Equal("broken catalog", await File.ReadAllTextAsync(Catalog.CatalogPath));
    }
    [Fact]
    public async Task RetiredHypervisorEntriesAreBackedUpAndFixesAndOriginalArchivesRemain()
    {
        var archive = MakeArchive(); Directory.CreateDirectory(Path.GetDirectoryName(Catalog.CatalogPath)!);
        var entries = new[] { new LocalGamePackage { Id = "retired", Kind = "Hypervisor", Name = "Old entry", ArchivePath = archive },
            new LocalGamePackage { Id = "kept", Kind = "Fix", Name = "Keep this fix", ArchivePath = archive } };
        var original = System.Text.Json.JsonSerializer.Serialize(entries); await File.WriteAllTextAsync(Catalog.CatalogPath, original);
        await Catalog.RetireLegacyEntriesAsync();
        Assert.Equal("kept", Assert.Single(await Catalog.ReadAsync()).Id); Assert.True(File.Exists(archive));
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Catalog.CatalogPath)!, "*.before-hypervisor-removal-*.json"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup));
    }
    [Fact]
    public async Task NewPackagesRejectExecutablePathsAndNonNumericSteamIds()
    {
        var package = new LocalGamePackage { Name = "Example", ArchivePath = Path.Combine(_folder, "script.cmd") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Catalog.UpsertAsync(package));
        await Assert.ThrowsAsync<InvalidDataException>(() => Catalog.UpsertAsync(package with { ArchivePath = MakeArchive(), AppId = "bad" }));
    }
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
