using Steamy.Services;

namespace Steamy.Tests;

public class ManifestLibraryScannerTests
{
    private static string Acf(int appId, string name, params (uint Depot, string Manifest)[] depots)
    {
        var depotText = string.Concat(depots.Select(depot =>
            $"\t\t\"{depot.Depot}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"{depot.Manifest}\"\n\t\t\t\"size\"\t\t\"1000\"\n\t\t}}\n"));
        return $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"{name}\"\n\t\"LastOwner\"\t\t\"76561198000000000\"\n" +
               $"\t\"InstalledDepots\"\n\t{{\n{depotText}\t}}\n}}\n";
    }

    [Fact]
    public void Steam_library_scan_matches_installed_depots_with_the_depot_cache()
    {
        using var temp = new TempFolder();
        var steamApps = Path.Combine(temp.Path, "steamapps");
        temp.Write("steamapps/appmanifest_730.acf", Acf(730, "Counter-Strike 2", (731, "111"), (732, "222")));
        temp.Write("depotcache/731_111.manifest", "manifest-bytes");

        var result = ManifestLibraryScanner.ScanSteamLibrary(temp.Path, new[] { steamApps });

        var app = Assert.Single(result);
        Assert.Equal(730, app.AppId);
        Assert.Equal("Counter-Strike 2", app.Name);
        Assert.Equal(ShareSourceKind.SteamLibrary, app.Source);
        Assert.Equal(2, app.Depots.Count);
        var file = Assert.Single(app.Files);
        Assert.Equal("731_111.manifest", file.EntryName);
        Assert.Equal(1, app.ManifestCount);
    }

    [Fact]
    public void Steam_library_scan_skips_apps_without_cached_manifests_and_shared_runtimes()
    {
        using var temp = new TempFolder();
        var steamApps = Path.Combine(temp.Path, "steamapps");
        temp.Write("steamapps/appmanifest_10.acf", Acf(10, "No Cache", (11, "999")));
        temp.Write("steamapps/appmanifest_228980.acf", Acf(228980, "Steamworks Common Redistributables", (228981, "5")));
        temp.Write("depotcache/228981_5.manifest", "x");

        Assert.Empty(ManifestLibraryScanner.ScanSteamLibrary(temp.Path, new[] { steamApps }));
    }

    [Fact]
    public void Library_folder_depot_cache_is_used_as_well()
    {
        using var temp = new TempFolder();
        var steamApps = Path.Combine(temp.Path, "Library", "steamapps");
        temp.Write("Library/steamapps/appmanifest_20.acf", Acf(20, "Second Drive", (21, "42")));
        temp.Write("Library/steamapps/depotcache/21_42.manifest", "m");

        var app = Assert.Single(ManifestLibraryScanner.ScanSteamLibrary(temp.Path, new[] { steamApps }));
        Assert.Equal(20, app.AppId);
    }

    [Fact]
    public void Dump_scan_picks_up_app_folders_and_only_dump_files()
    {
        using var temp = new TempFolder();
        temp.Write("app-10/10.lua", "addappid(10)");
        temp.Write("app-10/11_1.manifest", "m");
        temp.Write("app-10/notes.txt", "ignored");
        temp.Write("app-20/readme.md", "nothing to share");
        temp.Write("other/30.lua", "not an app folder");

        var result = ManifestLibraryScanner.ScanDumpRoots(new[] { temp.Path, temp.Path },
            appId => appId == 10 ? "Half-Life Fan Game" : null);

        var app = Assert.Single(result);
        Assert.Equal(10, app.AppId);
        Assert.Equal("Half-Life Fan Game", app.Name);
        Assert.Equal(ShareSourceKind.Dump, app.Source);
        Assert.True(app.HasLua);
        Assert.Equal(new[] { "10.lua", "11_1.manifest" }, app.Files.Select(file => file.EntryName));
    }

    [Theory]
    [InlineData("app-730", 730)]
    [InlineData("APP-1", 1)]
    [InlineData("app-", null)]
    [InlineData("app-0", null)]
    [InlineData("game-730", null)]
    public void App_folder_names_are_parsed(string name, int? expected) =>
        Assert.Equal(expected, ManifestLibraryScanner.ParseAppFolder(name));

    [Fact]
    public void Fingerprint_is_stable_and_follows_the_file_set()
    {
        var files = new[] { new ShareFile("a", "1_1.manifest", 10), new ShareFile("b", "1.lua", 3) };
        var first = new ShareCandidate(1, "A", ShareSourceKind.Dump, "x", files, Array.Empty<DepotManifestRef>(), DateTime.UtcNow);
        var reordered = new ShareCandidate(1, "Renamed", ShareSourceKind.Dump, "y", files.Reverse().ToList(), Array.Empty<DepotManifestRef>(), DateTime.MinValue);
        var updated = new ShareCandidate(1, "A", ShareSourceKind.Dump, "x",
            new[] { new ShareFile("a", "1_2.manifest", 10), files[1] }, Array.Empty<DepotManifestRef>(), DateTime.UtcNow);

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, updated.Fingerprint);
    }
}
