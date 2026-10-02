using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class SteamToolsTests
{
    private static byte[] Zip(params (string Name, string Contents)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var (name, contents) in entries)
            { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(contents); }
        return bytes.ToArray();
    }

    private static byte[] BinaryDepotManifest()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write(0x71F617D0u); writer.Write(0u); // Empty file payload; identity is in the metadata section.
        writer.Write(0x1F4812BEu); writer.Write(5u);
        writer.Write(new byte[] { 8, 0xE1, 3, 16, 123 }); // Depot 481; manifest 123.
        return bytes.ToArray();
    }

    [Fact]
    public async Task GameNamedBinaryManifestUsesItsActualDepotIdentityWithoutInventingAnAppId()
    {
        using var temp = new TempFolder();
        var bare = Path.Combine(temp.Path, "480.manifest");
        File.WriteAllBytes(bare, BinaryDepotManifest());
        var plan = await SteamToolsMetadata.ReadAsync([bare]);
        Assert.Empty(plan.AppIds);
        Assert.Equal("depotcache/481_123.manifest", Assert.Single(plan.Files).Key);
        var canonical = Path.Combine(temp.Path, "481_123.manifest");
        File.WriteAllBytes(canonical, BinaryDepotManifest());
        Assert.Single((await SteamToolsMetadata.ReadAsync([bare, canonical])).Files); // Identical copies are harmless.
    }

    [Fact]
    public async Task SourceArchiveSelectsAnAppNamedManifestByItsBinaryDepotInsteadOfItsWrongFilename()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "480.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("480.lua").Open())) writer.Write("addappid(480)\nsetManifestid(481,\"123\")");
            using var entry = zip.CreateEntry("480.manifest").Open();
            entry.Write(BinaryDepotManifest());
        }
        var plan = await SteamToolsMetadata.ReadAsync([path], 480);
        Assert.Equal(480, Assert.Single(plan.AppIds));
        Assert.Equal(1, plan.ManifestCount);
        Assert.Contains("depotcache/481_123.manifest", plan.Files.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidOrTruncatedBinaryManifestCannotInventAFileIdentity(int mutation)
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "480.manifest");
        var bytes = BinaryDepotManifest();
        if (mutation == 0) bytes[0] = 0;
        if (mutation == 1) bytes[4] = 255;
        if (mutation == 2) bytes = bytes[..^1];
        File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([path]));
    }

    [Theory]
    [InlineData("480.7z")]
    [InlineData("480.rar")]
    public async Task CompressedMetadataImportsLuaAndManifestsAndIgnoresExecutables(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var plan = await SteamToolsMetadata.ReadAsync([path]);
        Assert.Equal(480, Assert.Single(plan.AppIds));
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains("setManifestid(481,\"123\")", Encoding.UTF8.GetString(plan.Files["config/stplug-in/480.lua"]));
        Assert.Equal("manifest fixture", Encoding.UTF8.GetString(plan.Files["depotcache/481_123.manifest"]));
        Assert.DoesNotContain(plan.Files.Keys, file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([path], 999));
    }

    [Fact]
    public async Task WindowsArchiveTraversalIsRejectedBeforeImport()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "unsafe-metadata.rar");
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([path]));
    }

    [Fact]
    public async Task EncryptedMetadataArchiveReportsExtractionRequirement()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fix-password.7z");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([path]));
        Assert.Contains("Encrypted", exception.Message);
    }

    [Fact]
    public async Task SushiStyleZipAndLooseFilesDetectAppsAndUseTheRealSteamFolders()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "480.zip");
        File.WriteAllBytes(zip, Zip(("nested/480.lua", "addappid(480)\naddappid(481,0,\"aabbccddeeff0011\")\nsetManifestid(481,\"123\")"), ("nested/481_123.manifest", "manifest bytes"), ("nested/ignored.exe", "never installed")));
        var plan = await SteamToolsMetadata.ReadAsync([zip]);
        Assert.Equal(480, Assert.Single(plan.AppIds));
        Assert.Equal(1, plan.ManifestCount);
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains("config/stplug-in/480.lua", plan.Files.Keys);
        Assert.Contains("depotcache/481_123.manifest", plan.Files.Keys);
        var lua = temp.Write("other.lua", "-- addappid(999)\naddappid(100)\nsetManifestid(101,\"456\")");
        plan = await SteamToolsMetadata.ReadAsync([lua]);
        Assert.Equal(100, Assert.Single(plan.AppIds));
        Assert.DoesNotContain("999", Encoding.UTF8.GetString(plan.Files["config/stplug-in/100.lua"]));
    }

    [Fact]
    public async Task ManifestOnlyImportDoesNotInventAnAppId()
    {
        using var temp = new TempFolder();
        var path = temp.Write("481_123.manifest", "metadata");
        var plan = await SteamToolsMetadata.ReadAsync([path]);
        Assert.Empty(plan.AppIds);
        Assert.Equal("depotcache/481_123.manifest", Assert.Single(plan.Files).Key);
    }

    [Theory]
    [InlineData("addappid(480)\nos.execute('bad')")]
    [InlineData("addappid(480)\nsetManifestid(481,\"not-an-id\")")]
    [InlineData("addappid(480)\nsetManifestid(481,\"0\")")]
    [InlineData("addappid(480)\nsetManifestid(481,\"123\"")]
    [InlineData("addappid(480)\naddappid(481)")]
    public void UnsupportedAndAmbiguousLuaCannotBecomeInstalledScripts(string lua)
        => Assert.Throws<InvalidDataException>(() => SteamToolsMetadata.ReadLua(lua, "unnamed.lua"));

    [Fact]
    public async Task AmbiguousLuaRequiresExplicitIdAndBundleSelectionDoesNotMixGames()
    {
        var script = "addappid(480)\naddappid(481)";
        Assert.Equal(480, SteamToolsMetadata.ReadLua(script, "unknown.lua", 480).AppId);
        Assert.Throws<InvalidDataException>(() => SteamToolsMetadata.ReadLua(script, "unknown.lua", 999));
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "games.zip");
        File.WriteAllBytes(zip, Zip(("a/100.lua", "addappid(100)\nsetManifestid(101,\"123\")"), ("a/101_123.manifest", "first"), ("b/200.lua", "addappid(200)\nsetManifestid(201,\"456\")"), ("b/201_456.manifest", "second")));
        var plan = await SteamToolsMetadata.ReadAsync([zip], 200);
        Assert.Equal(200, Assert.Single(plan.AppIds));
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains("depotcache/201_456.manifest", plan.Files.Keys);
        Assert.DoesNotContain("depotcache/101_123.manifest", plan.Files.Keys);
        Assert.Equal(2, (await SteamToolsMetadata.ReadAsync([zip])).AppIds.Count);
    }

    [Theory]
    [InlineData("../480.lua")]
    [InlineData("folder\\480.lua")]
    [InlineData("CON.lua")]
    [InlineData("/480.lua")]
    public async Task UnsafeArchivePathsAreRejectedBeforeAnySteamWrite(string entry)
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "bad.zip");
        File.WriteAllBytes(zip, Zip((entry, "addappid(480)")));
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([zip]));
    }

    [Fact]
    public async Task ConflictingFilesFailInsteadOfReplacingOneGamesMetadataWithAnotherCopy()
    {
        using var temp = new TempFolder();
        var first = temp.Write("a/480.lua", "addappid(480)");
        var second = temp.Write("b/480.lua", "addappid(480)\nsetManifestid(481,\"123\")");
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamToolsMetadata.ReadAsync([first, second]));
    }

    [Fact]
    public void BackendDetectionRequiresMatchingPayloadHashesAndDetectsDamage()
    {
        using var temp = new TempFolder();
        var files = SteamToolsBackend.Official104.Keys.ToDictionary(name => name, _ => Encoding.UTF8.GetBytes("MZoffline fixture"));
        foreach (var file in files) temp.Write(file.Key, Encoding.UTF8.GetString(file.Value));
        Assert.False(SteamToolsBackend.IsInstalled(temp.Path));
        var receipt = temp.Write(SteamToolsBackend.ReceiptPath, Encoding.UTF8.GetString(SteamToolsBackend.CreateReceipt("test", files)));
        Assert.True(SteamToolsBackend.IsInstalled(temp.Path));
        temp.Write("OpenSteamTool.dll", "MZdifferent backend");
        Assert.False(SteamToolsBackend.IsInstalled(temp.Path));
        File.WriteAllBytes(Path.Combine(temp.Path, "OpenSteamTool.dll"), files["OpenSteamTool.dll"]);
        File.Delete(Path.Combine(temp.Path, "xinput1_4.dll"));
        Assert.False(SteamToolsBackend.IsInstalled(temp.Path));
        File.WriteAllText(receipt, "{invalid receipt");
        Assert.False(SteamToolsBackend.IsInstalled(temp.Path));
    }

    [Fact]
    public void BackendPayloadRequiresItsOfficialDigestAndAllThreeDlls()
    {
        var path = Environment.GetEnvironmentVariable("STEAMY_BST_TEST_ARCHIVE");
        var bytes = string.IsNullOrEmpty(path) ? Zip(("dwmapi.dll", "MZloader"), ("xinput1_4.dll", "MZloader"), ("OpenSteamTool.dll", "MZbackend"), ("extra.lib", "ignored")) : File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var payload = SteamToolsBackend.ReadArchive(bytes, hash);
        Assert.Equal(3, payload.Count);
        Assert.Throws<InvalidDataException>(() => SteamToolsBackend.ReadArchive(bytes, new string('0',64)));
        var incomplete = Zip(("OpenSteamTool.dll", "MZbackend"));
        Assert.Throws<InvalidDataException>(() => SteamToolsBackend.ReadArchive(incomplete, Convert.ToHexString(SHA256.HashData(incomplete))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[lua]")]
    [InlineData("[lua]\n# existing settings\n")]
    [InlineData("[lua]\npaths = [\"custom#folder\"] # retain this\n[other]\nenabled = true\n")]
    [InlineData("[lua]\npaths = [\n  'custom', # comment with \"quotes\" and ]\n]\n[other]\nx=1\n")]
    [InlineData("[lua]\npaths = [] # empty\n")]
    public void LuaFolderRegistrationPreservesSettingsAndIsIdempotent(string config)
    {
        var updated = SteamToolsFiles.RegisterLuaPath(config);
        Assert.Contains("config/stplug-in", updated);
        Assert.Equal(updated, SteamToolsFiles.RegisterLuaPath(updated));
        if (config.Contains("[other]")) Assert.Contains(config[config.IndexOf("[other]")..], updated);
        if (config.Contains("custom")) Assert.Contains("custom", updated);
    }

    [Theory]
    [InlineData("[lua]\npaths = [\"custom\"\n")]
    [InlineData("[lua]\npaths = \"custom\"\n")]
    public void BrokenConfigurationIsRejectedBeforeItCanBeOverwritten(string config)
        => Assert.Throws<InvalidDataException>(() => SteamToolsFiles.RegisterLuaPath(config));

    [Fact]
    public void AWriteFailureRestoresOldFilesAndRemovesNewFiles()
    {
        using var temp = new TempFolder();
        temp.Write("steam.exe", "fixture only");
        var old = temp.Write("config/stplug-in/480.lua", "original");
        var files = new Dictionary<string, byte[]> { ["config/stplug-in/480.lua"] = Encoding.UTF8.GetBytes("replacement"), ["depotcache/481_123.manifest"] = Encoding.UTF8.GetBytes("new") };
        var writes = 0;
        Assert.Throws<IOException>(() => SteamToolsFiles.Apply(temp.Path, files, (path, bytes) => { File.WriteAllBytes(path, bytes); if (++writes == 2) throw new IOException("injected write failure"); }));
        Assert.Equal("original", File.ReadAllText(old));
        Assert.False(File.Exists(Path.Combine(temp.Path,"depotcache/481_123.manifest")));
        Assert.Equal("original", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(temp.Path,"config/steamy-backups"),"480.lua",SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public void CancellationAndLinkedTargetsDoNotTouchTheOtherFolder()
    {
        using var temp = new TempFolder();
        using var outside = new TempFolder();
        temp.Write("steam.exe", "fixture only");
        var file = outside.Write("480.lua", "external original");
        Directory.CreateDirectory(Path.Combine(temp.Path,"config"));
        var linked = Path.Combine(temp.Path,"config", "stplug-in");
        if (OperatingSystem.IsWindows())
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", linked, outside.Path }) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            Assert.True(process.WaitForExit(5000));
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        }
        else Directory.CreateSymbolicLink(linked, outside.Path);
        var files = new Dictionary<string, byte[]> { ["config/stplug-in/480.lua"] = Encoding.UTF8.GetBytes("replacement") };
        Assert.Throws<IOException>(() => SteamToolsFiles.Apply(temp.Path, files));
        Assert.Equal("external original", File.ReadAllText(file));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SteamToolsFiles.Apply(temp.Path, new Dictionary<string, byte[]> { ["opensteamtool.toml"] = Encoding.UTF8.GetBytes("new") }, token:cancellation.Token));
        Assert.False(File.Exists(Path.Combine(temp.Path,"opensteamtool.toml")));
    }
}
