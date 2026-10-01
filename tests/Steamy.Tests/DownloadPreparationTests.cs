using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadPreparationTests
{
    [Fact]
    public void ListsSourceManifestVersionsAndKeepsTheLuaDefault()
    {
        var catalog = DownloadPreparationReader.Read("""
            addappid(101, 1, "aabbccddeeff0011")
            setManifestid(101, "999", 2048)
            setManifestid(102, "18446744073709551615")
            """, manifestFileNames: ["101_111.manifest", "101_999.manifest", "103_777.manifest"]);
        Assert.Equal(new[] { 101, 102, 103 }, catalog.Depots.Select(depot => depot.DepotId));
        var first = catalog.Depots[0];
        Assert.Equal("999", first.DefaultManifestId);
        Assert.Equal(new[] { "999", "111" }, first.Versions.Select(version => version.ManifestId));
        Assert.Equal(2048, first.Versions[0].SizeBytes);
        Assert.Null(first.Versions[1].SizeBytes);
        Assert.Equal("aabbccddeeff0011", catalog.Manifests.First().DecryptionKey);
    }

    [Fact]
    public void MissingBuildMetadataNeverBecomesAnInventedBuildNumber()
    {
        var catalog = DownloadPreparationReader.Read("setManifestid(101, \"1234567890123456789\")");
        var version = Assert.Single(Assert.Single(catalog.Depots).Versions);
        Assert.Null(version.BuildLabel);
        Assert.Equal("Manifest 1234567890123456789", version.Label);
    }

    [Theory]
    [InlineData("-- Build ID: 98123", "Build 98123")]
    [InlineData("-- game version: 1.8.2", "Version 1.8.2")]
    public void DisplaysOnlyExplicitSourceBuildOrVersionMetadata(string metadata, string expected)
    {
        var catalog = DownloadPreparationReader.Read(metadata + "\nsetManifestid(101, \"111\")",
            manifestFileNames: ["101_222.manifest"]);
        var versions = Assert.Single(catalog.Depots).Versions;
        Assert.Equal(expected, versions[0].BuildLabel);
        Assert.Null(versions[1].BuildLabel);
    }

    [Fact]
    public void SelectingASubsetAndAnOlderManifestUsesOnlyThatExactVersion()
    {
        var catalog = DownloadPreparationReader.Read("setManifestid(101, \"999\")\nsetManifestid(102, \"888\")",
            manifestFileNames: ["101_111.manifest"]);
        var selected = DownloadPreparationReader.Select(catalog, [new(101, "111")]);
        var depot = Assert.Single(selected);
        Assert.Equal(101, depot.DepotId);
        Assert.Equal("111", depot.ManifestId);
    }

    [Fact]
    public void CannotSelectAVersionFromAnotherSourceOrTwoVersionsOfOneDepot()
    {
        var catalog = DownloadPreparationReader.Read("setManifestid(101, \"999\")",
            manifestFileNames: ["101_111.manifest"]);
        Assert.Throws<ArgumentException>(() => DownloadPreparationReader.Select(catalog, [new(101, "777")]));
        Assert.Throws<ArgumentException>(() => DownloadPreparationReader.Select(catalog, [new(101, "111"), new(101, "999")]));
        Assert.Throws<ArgumentException>(() => DownloadPreparationReader.Select(catalog, []));
    }

    [Fact]
    public void IgnoresCommentedCallsAndRejectsOverflowedOrZeroIdentifiers()
    {
        var catalog = DownloadPreparationReader.Read("""
            -- setManifestid(101, "111")
            --[[
            setManifestid(104, "444")
            ]]
            setManifestid(9999999999999999999999, "111")
            setManifestid(101, "18446744073709551616")
            setManifestid(101, "0")
            setManifestid(0, "111")
            setManifestid(102, "222") -- setManifestid(103, "333")
            """, manifestFileNames: ["999999999999999999_111.manifest", "101_0.manifest"]);
        Assert.Equal(102, Assert.Single(catalog.Depots).DepotId);
    }

    [Fact]
    public void KeyFilesFillMissingKeysWithoutReplacingLuaKeys()
    {
        var catalog = DownloadPreparationReader.Read("addappid(101,1,\"aabbccddeeff0011\")\nsetManifestid(101,\"111\")\nsetManifestid(102,\"222\")",
            fileKeys: new Dictionary<int, string> { [101] = "wrong-key", [102] = "1122334455667788" });
        Assert.Equal("aabbccddeeff0011", catalog.Manifests[0].DecryptionKey);
        Assert.Equal("1122334455667788", catalog.Manifests[1].DecryptionKey);
    }

    [Fact]
    public void AdditionalScriptsSupplyVersionsWithoutChangingThePrimaryDefault()
    {
        var catalog = DownloadPreparationReader.Read("setManifestid(101, \"999\")",
            ["setManifestid(101, \"111\")\nsetManifestid(102, \"222\")"]);
        Assert.Equal("999", catalog.Depots[0].DefaultManifestId);
        Assert.Equal(2, catalog.Depots[0].Versions.Count);
    }

    [Fact]
    public void OlderScriptsCannotReplacePrimaryBuildMetadataForTheSameManifest()
    {
        var catalog = DownloadPreparationReader.Read("-- Build ID: 900\nsetManifestid(101, \"999\", 2048)",
            ["-- Build ID: 100\nsetManifestid(101, \"999\", 1024)"]);
        var version = Assert.Single(Assert.Single(catalog.Depots).Versions);
        Assert.Equal("Build 900", version.BuildLabel);
        Assert.Equal(2048, version.SizeBytes);
    }

    [Fact]
    public void AKeyFileCannotInjectAnInvalidDepotKeyIntoThePreparedSnapshot()
    {
        var catalog = DownloadPreparationReader.Read("setManifestid(101, \"111\")\nsetManifestid(102, \"222\")",
            fileKeys: new Dictionary<int, string> { [101] = "invalid\nkey", [102] = "1122334455667788" });
        Assert.Equal(string.Empty, catalog.Manifests[0].DecryptionKey);
        Assert.Equal("1122334455667788", catalog.Manifests[1].DecryptionKey);
    }

    [Fact]
    public void BoundsAllSourceMetadataBeforeCreatingASelection()
    {
        Assert.Throws<InvalidDataException>(() => DownloadPreparationReader.Read(new string(' ', DownloadPreparationReader.MaximumLuaCharacters + 1)));
        Assert.Throws<InvalidDataException>(() => DownloadPreparationReader.Read("", manifestFileNames: Enumerable.Repeat("101_111.manifest", DownloadPreparationReader.MaximumEntries + 1)));
    }

    [Theory]
    [InlineData("100.lua", true)]
    [InlineData("101_123.manifest", true)]
    [InlineData("../100.lua", false)]
    [InlineData("..\\100.lua", false)]
    [InlineData("C:\\100.lua", false)]
    [InlineData("100.lua:extra", false)]
    [InlineData("/100.lua", false)]
    public void SourceMetadataNamesCannotEscapeTheirAppPackage(string name, bool expected) =>
        Assert.Equal(expected, DownloadPreparationReader.IsSafePackageFileName(name));

    [Fact]
    public async Task SavedResumeStateRetainsTheChosenSubsetWhenSourceDefaultChanges()
    {
        var folder = Path.Combine(Path.GetTempPath(), "steamy-selected-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = DownloadPreparationReader.Read("setManifestid(101, \"999\")\nsetManifestid(102, \"888\")",
                manifestFileNames: ["101_111.manifest"]);
            var selected = DownloadPreparationReader.Select(catalog, [new(101, "111")]);
            var saved = selected.Select(depot => new CachedDepotManifest(depot.DepotId, depot.ManifestId)).ToArray();
            await DepotResumeStateStore.WriteAsync(folder, new(100, folder, saved), default);
            _ = DownloadPreparationReader.Read("setManifestid(101, \"1000\")\nsetManifestid(102, \"2000\")");
            var pinned = Assert.Single(DepotResumeStateStore.Read(folder, 100, folder)!.Depots);
            Assert.Equal(new CachedDepotManifest(101, "111"), pinned);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
