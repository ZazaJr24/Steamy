using System.IO.Compression;
using Steamy.Services;

namespace Steamy.Tests;

public class LocalManifestPackageTests
{
    private static ShareCandidate Candidate(TempFolder temp, int id)
    {
        var lua = temp.Write($"{id}/{id}.lua", $"addappid({id})\naddappid({id+1},1,\"aabbccddeeff0011\")\nsetManifestid({id+1},\"123\")");
        var manifest = temp.Write($"{id}/{id+1}_123.manifest", "original manifest");
        return new(id, $"Game {id}", ShareSourceKind.Lua, temp.Path,
            [new(lua,$"{id}.lua",new FileInfo(lua).Length), new(manifest,$"{id+1}_123.manifest",new FileInfo(manifest).Length)],
            [new((uint)id+1,"123",10)],DateTime.UtcNow);
    }
    [Fact]
    public async Task MultiAppRoundtripSelectsOnlyOneAppAndOwnsItsFilesAfterZipIsDeleted()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "bundle.zip");
        ShareArchiveBuilder.WriteBundleAtomically(zip,[Candidate(temp,100),Candidate(temp,200)],"0.4.6",DateTime.UtcNow);
        var imported = await LocalManifestPackage.ImportAsync(zip,100,Path.Combine(temp.Path,"prepared"));
        File.Delete(zip);
        Assert.Contains("addappid(100)",imported.Lua);
        Assert.Equal("original manifest",File.ReadAllText(Path.Combine(imported.Directory,"101_123.manifest")));
        Assert.False(File.Exists(Path.Combine(imported.Directory,"201_123.manifest")));
        var catalog = DownloadPreparationReader.Read(imported.Lua,manifestFileNames:Directory.EnumerateFiles(imported.Directory,"*.manifest"));
        Assert.Equal("123",Assert.Single(catalog.Depots).DefaultManifestId);
    }
    [Fact]
    public async Task DamagedHashIsRejectedAndPartialImportIsRemoved()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path,"damaged.zip");
        File.WriteAllBytes(zip,ShareArchiveBuilder.BuildAppArchive(Candidate(temp,100),"0.4.6",DateTime.UtcNow));
        using (var archive = ZipFile.Open(zip,ZipArchiveMode.Update))
        {
            archive.GetEntry("101_123.manifest")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("101_123.manifest").Open());
            writer.Write("modified content");
        }
        var destination = Path.Combine(temp.Path,"prepared");
        var error = await Assert.ThrowsAsync<InvalidDataException>(()=>LocalManifestPackage.ImportAsync(zip,100,destination));
        Assert.Contains("Hash",error.Message);
        Assert.False(Directory.Exists(destination));
    }
    [Theory]
    [InlineData("../evil.lua")]
    [InlineData("/evil.lua")]
    [InlineData("C:/evil.lua")]
    [InlineData("folder\\evil.lua")]
    [InlineData("CON.lua")]
    [InlineData("evil.lua.")]
    [InlineData("folder/../evil.txt")]
    public async Task UnsafePathsAreRejectedEvenForNonMetadataEntries(string name)
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path,"unsafe.zip");
        using(var archive = ZipFile.Open(zip,ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("addappid(100)"); }
        var destination = Path.Combine(temp.Path,"prepared");
        await Assert.ThrowsAsync<InvalidDataException>(()=>LocalManifestPackage.ImportAsync(zip,100,destination));
        Assert.False(Directory.Exists(destination));
    }
    [Fact]
    public async Task DuplicateNamesAndAmbiguousAppsAreRejected()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path,"duplicate.zip");
        using(var archive = ZipFile.Open(zip,ZipArchiveMode.Create))
            foreach(var name in new[]{"100.lua","100.LUA"})
            { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("addappid(100)\nsetManifestid(101,\"123\")"); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>LocalManifestPackage.ImportAsync(zip,100,Path.Combine(temp.Path,"p1")));
        var lua = temp.Write("ambiguous.lua","addappid(100)\naddappid(200)\nsetManifestid(101,\"123\")");
        await Assert.ThrowsAsync<InvalidDataException>(()=>LocalManifestPackage.ImportAsync(lua,100,Path.Combine(temp.Path,"p2")));
    }
    [Fact]
    public async Task WrongGameAndCancelledImportLeaveNoFiles()
    {
        using var temp = new TempFolder();
        var lua = temp.Write("100.lua","addappid(100)\nsetManifestid(101,\"123\")");
        await Assert.ThrowsAsync<InvalidDataException>(()=>LocalManifestPackage.ImportAsync(lua,200,Path.Combine(temp.Path,"wrong")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>LocalManifestPackage.ImportAsync(lua,100,Path.Combine(temp.Path,"cancel"),cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(temp.Path,"cancel")));
    }
    [Fact]
    public void FailedOrCancelledExportPreservesExistingZipAndCleansTemporaryFiles()
    {
        using var temp = new TempFolder();
        var zip = temp.Write("existing.zip","previous archive");
        var candidate = Candidate(temp,100);
        File.Delete(candidate.Files[0].FullPath);
        Assert.Throws<FileNotFoundException>(()=>ShareArchiveBuilder.WriteBundleAtomically(zip,[candidate],"0.4.6",DateTime.UtcNow));
        Assert.Equal("previous archive",File.ReadAllText(zip));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>ShareArchiveBuilder.WriteBundleAtomically(zip,[Candidate(temp,200)],"0.4.6",DateTime.UtcNow,cancellation.Token));
        Assert.Equal("previous archive",File.ReadAllText(zip));
        Assert.Empty(Directory.GetFiles(temp.Path,"*.partial"));
    }
    [Fact]
    public async Task SteamLibraryMetadataWithoutLuaCanRoundtrip()
    {
        using var temp = new TempFolder();
        var file = temp.Write("101_123.manifest","manifest");
        var candidate = new ShareCandidate(100,"Game",ShareSourceKind.SteamLibrary,temp.Path,[new(file,"101_123.manifest",8)],[new(101,"123",50)],DateTime.UtcNow);
        var zip = Path.Combine(temp.Path,"library.zip");
        File.WriteAllBytes(zip,ShareArchiveBuilder.BuildAppArchive(candidate,"0.4.6",DateTime.UtcNow));
        var imported = await LocalManifestPackage.ImportAsync(zip,100,Path.Combine(temp.Path,"prepared"));
        Assert.Equal(101,Assert.Single(DownloadPreparationReader.Read(imported.Lua).Depots).DepotId);
    }
}
