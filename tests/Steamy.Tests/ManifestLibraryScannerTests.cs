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

    private static ShareScanInput Input(TempFolder temp, params string[] luaFolders) =>
        new(temp.Path, new[] { Path.Combine(temp.Path, "steamapps") },
            luaFolders.Select(folder => Path.Combine(temp.Path, folder)).ToList(), Array.Empty<string>());

    [Fact]
    public void Installed_games_are_matched_with_the_depot_cache()
    {
        using var temp = new TempFolder();
        temp.Write("steamapps/appmanifest_730.acf", Acf(730, "Counter-Strike 2", (731, "111"), (732, "222")));
        temp.Write("depotcache/731_111.manifest", "manifest-bytes");

        var app = Assert.Single(ManifestLibraryScanner.ScanAll(Input(temp)));

        Assert.Equal(730, app.AppId);
        Assert.Equal("Counter-Strike 2", app.Name);
        Assert.Equal(ShareSourceKind.SteamLibrary, app.Source);
        Assert.Equal(2, app.Depots.Count);
        Assert.Equal("731_111.manifest", Assert.Single(app.Files).EntryName);
    }

    [Fact]
    public void Games_that_are_not_installed_are_found_through_their_cached_manifests()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps"));
        temp.Write("depotcache/1245621_900.manifest", "a");
        temp.Write("depotcache/1245622_901.manifest", "b");
        temp.Write("depotcache/not-a-manifest.txt", "c");

        var result = ManifestLibraryScanner.ScanAll(Input(temp));

        Assert.Equal(new[] { 1245621, 1245622 }, result.Select(app => app.AppId).OrderBy(id => id));
        Assert.All(result, app => Assert.Equal(ShareSourceKind.Manifests, app.Source));
        Assert.Contains(result, app => app.Name == "Depot 1245621");
    }

    [Fact]
    public void The_app_list_assigns_loose_manifests_to_their_game_and_names_it()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps"));
        temp.Write("depotcache/1245621_900.manifest", "a");
        temp.Write("depotcache/1245622_901.manifest", "b");
        var list = temp.Write("appid.json", """[{"appid": 1245600, "name": "Other"}, {"appid": 1245620, "name": "ELDEN RING"}]""");

        var app = Assert.Single(ManifestLibraryScanner.ScanAll(Input(temp), new AppListIndex(list)));

        Assert.Equal(1245620, app.AppId);
        Assert.Equal("ELDEN RING", app.Name);
        Assert.Equal(2, app.ManifestCount);
    }

    [Fact]
    public void Lua_scripts_bring_their_pinned_manifests_even_for_games_that_are_not_installed()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps"));
        temp.Write("config/stplug-in/1086940.lua", """
            -- Baldur's Gate 3
            addappid(1086940)
            addappid(1086941, 1, "abcdef")
            setManifestid(1086941, "5555", 0)
            addappid(1086942, 1, "123456")
            setManifestid(1086942, "6666", 0)
            """);
        temp.Write("config/depotcache/1086941_5555.manifest", "m1");
        temp.Write("depotcache/1086942_6666.manifest", "m2");
        temp.Write("depotcache/1086942_1111.manifest", "older manifest, not pinned");

        var result = ManifestLibraryScanner.ScanAll(Input(temp, "config/stplug-in"));

        var app = Assert.Single(result, candidate => candidate.AppId == 1086940);
        Assert.Equal(ShareSourceKind.Lua, app.Source);
        Assert.True(app.HasLua);
        Assert.Equal(new[] { "1086940.lua", "1086941_5555.manifest", "1086942_6666.manifest" },
            app.Files.Select(file => file.EntryName).OrderBy(name => name, StringComparer.Ordinal));
        // The unpinned older manifest is still shareable on its own.
        Assert.Contains(result, candidate => candidate.Files.Any(file => file.EntryName == "1086942_1111.manifest"));
    }

    [Fact]
    public void A_lua_without_pins_takes_the_manifests_of_its_depots_next_to_it()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps"));
        temp.Write("work/hubcap/570/570.lua", "addappid(570)\naddappid(571, 1, \"key\")\n");
        temp.Write("work/hubcap/570/571_42.manifest", "m");
        temp.Write("work/hubcap/570/999_1.manifest", "belongs to something else");

        var app = Assert.Single(ManifestLibraryScanner.ScanAll(Input(temp, "work")), candidate => candidate.AppId == 570);

        Assert.Equal(new[] { "570.lua", "571_42.manifest" }, app.Files.Select(file => file.EntryName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Installed_game_with_a_lua_becomes_one_entry_with_both()
    {
        using var temp = new TempFolder();
        temp.Write("steamapps/appmanifest_730.acf", Acf(730, "Counter-Strike 2", (731, "111")));
        temp.Write("depotcache/731_111.manifest", "m");
        temp.Write("config/stplug-in/730.lua", "addappid(730)\naddappid(731,1,\"k\")\nsetManifestid(731,\"111\")\n");

        var app = Assert.Single(ManifestLibraryScanner.ScanAll(Input(temp, "config/stplug-in")));

        Assert.Equal(ShareSourceKind.SteamLibrary, app.Source);
        Assert.Equal("Counter-Strike 2", app.Name);
        Assert.True(app.HasLua);
        Assert.Equal(2, app.Files.Count);
    }

    [Fact]
    public void The_newest_lua_wins_when_an_app_has_several()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps"));
        var old = temp.Write("a/10.lua", "addappid(10) -- old");
        var fresh = temp.Write("b/10.lua", "addappid(10) -- new");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        var app = Assert.Single(ManifestLibraryScanner.ScanAll(Input(temp, "a", "b")));

        Assert.Equal(fresh, Assert.Single(app.Files).FullPath);
    }

    [Fact]
    public void Shared_runtimes_are_left_out()
    {
        using var temp = new TempFolder();
        temp.Write("steamapps/appmanifest_228980.acf", Acf(228980, "Steamworks Common Redistributables", (228981, "5")));
        temp.Write("depotcache/228981_5.manifest", "x");
        var list = temp.Write("appid.json", """[{"appid": 228980, "name": "Steamworks Common Redistributables"}]""");

        Assert.Empty(ManifestLibraryScanner.ScanAll(Input(temp), new AppListIndex(list)));
    }

    [Theory]
    [InlineData("addappid(730)\naddappid(731,1,\"k\")", 730, new uint[] { 731 })]
    [InlineData("addappid( 440 , 1, \"k\")\nsetManifestid(441, \"9\", 0)", 440, new uint[] { 441 })]
    [InlineData("setManifestid(11, \"1\")", null, new uint[] { 11 })]
    public void Lua_scripts_are_parsed(string text, int? expectedApp, uint[] expectedDepots)
    {
        var script = ManifestLibraryScanner.ParseLua(text);

        if (expectedApp is null)
        {
            Assert.Null(script);
            return;
        }

        Assert.NotNull(script);
        Assert.Equal(expectedApp, script!.AppId);
        Assert.Equal(expectedDepots, script.Depots.OrderBy(depot => depot));
    }

    [Fact]
    public void A_lua_without_addappid_takes_the_app_from_its_file_name()
    {
        var script = ManifestLibraryScanner.ParseLua("setManifestid(1245621, \"1\")", "1245620.lua");

        Assert.Equal(1245620, script!.AppId);
    }

    [Theory]
    [InlineData("app-730", 730)]
    [InlineData("APP-1", 1)]
    [InlineData("app-", null)]
    [InlineData("app-0", null)]
    [InlineData("game-730", null)]
    public void Old_dump_folder_names_are_parsed(string name, int? expected) =>
        Assert.Equal(expected, ManifestLibraryScanner.ParseAppFolder(name));

    [Fact]
    public void Fingerprint_is_stable_and_follows_the_file_set()
    {
        var files = new[] { new ShareFile("a", "1_1.manifest", 10), new ShareFile("b", "1.lua", 3) };
        var first = new ShareCandidate(1, "A", ShareSourceKind.Lua, "x", files, Array.Empty<DepotManifestRef>(), DateTime.UtcNow);
        var reordered = new ShareCandidate(1, "Renamed", ShareSourceKind.Lua, "y", files.Reverse().ToList(), Array.Empty<DepotManifestRef>(), DateTime.MinValue);
        var updated = new ShareCandidate(1, "A", ShareSourceKind.Lua, "x",
            new[] { new ShareFile("a", "1_2.manifest", 10), files[1] }, Array.Empty<DepotManifestRef>(), DateTime.UtcNow);

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, updated.Fingerprint);
    }
}
