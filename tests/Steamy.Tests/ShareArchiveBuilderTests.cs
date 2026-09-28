using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Steamy.Services;

namespace Steamy.Tests;

public class ShareArchiveBuilderTests
{
    [Fact]
    public void Acf_cleaning_removes_account_and_local_path_lines_only()
    {
        const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"LastOwner\"\t\t\"76561198000000000\"\n" +
                           "\t\"LauncherPath\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\\\\steam.exe\"\n\t\"buildid\"\t\t\"123\"\n}\n";

        var cleaned = Encoding.UTF8.GetString(ShareArchiveBuilder.SanitizeAcf(Encoding.UTF8.GetBytes(acf)));

        Assert.DoesNotContain("LastOwner", cleaned);
        Assert.DoesNotContain("7656119", cleaned);
        Assert.DoesNotContain("LauncherPath", cleaned);
        Assert.Contains("\"appid\"", cleaned);
        Assert.Contains("\"buildid\"", cleaned);
    }

    [Fact]
    public void App_archive_holds_the_files_and_describes_them_in_steamy_json()
    {
        using var temp = new TempFolder();
        var lua = temp.Write("app-730/730.lua", "addappid(730)");
        var acf = temp.Write("app-730/appmanifest_730.acf", "\"AppState\"\n{\n\t\"appid\" \"730\"\n\t\"LastOwner\" \"76561198000000000\"\n}\n");
        var candidate = new ShareCandidate(730, "Counter-Strike 2", ShareSourceKind.Dump, Path.Combine(temp.Path, "app-730"),
            new[] { new ShareFile(lua, "730.lua", 13), new ShareFile(acf, "appmanifest_730.acf", new FileInfo(acf).Length) },
            new[] { new DepotManifestRef(731, "111", 5) }, DateTime.UtcNow);

        var bytes = ShareArchiveBuilder.BuildAppArchive(candidate, "0.2.7", new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(new[] { "730.lua", "README.txt", "appmanifest_730.acf", "steamy.json" },
            zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));

        using var acfReader = new StreamReader(zip.GetEntry("appmanifest_730.acf")!.Open());
        Assert.DoesNotContain("LastOwner", acfReader.ReadToEnd());

        using var json = JsonDocument.Parse(zip.GetEntry("steamy.json")!.Open());
        var root = json.RootElement;
        Assert.Equal(730, root.GetProperty("appId").GetInt32());
        Assert.Equal("dump", root.GetProperty("source").GetString());
        Assert.Equal("111", root.GetProperty("depots")[0].GetProperty("manifest").GetString());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal(64, root.GetProperty("files")[0].GetProperty("sha256").GetString()!.Length);
        Assert.DoesNotContain(temp.Path, root.GetRawText());
    }

    [Fact]
    public void Bundle_gives_every_app_its_own_folder_and_an_index()
    {
        using var temp = new TempFolder();
        var a = temp.Write("a/1_1.manifest", "a");
        var b = temp.Write("b/2_2.manifest", "b");
        var items = new[]
        {
            new ShareCandidate(1, "Same: Name", ShareSourceKind.SteamLibrary, temp.Path, new[] { new ShareFile(a, "1_1.manifest", 1) }, Array.Empty<DepotManifestRef>(), DateTime.UtcNow),
            new ShareCandidate(1, "Same: Name", ShareSourceKind.Dump, temp.Path, new[] { new ShareFile(b, "2_2.manifest", 1) }, Array.Empty<DepotManifestRef>(), DateTime.UtcNow)
        };

        using var buffer = new MemoryStream();
        ShareArchiveBuilder.WriteBundle(buffer, items, "0.2.7", DateTime.UtcNow);
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("1 - Same Name/1_1.manifest", names);
        Assert.Contains("1 - Same Name (2)/2_2.manifest", names);
        Assert.Contains("index.json", names);

        using var index = JsonDocument.Parse(zip.GetEntry("index.json")!.Open());
        Assert.Equal(2, index.RootElement.GetProperty("apps").GetArrayLength());
    }

    [Theory]
    [InlineData("Half-Life 2: Episode One", "Half-Life 2 Episode One")]
    [InlineData("  ", "App")]
    [InlineData("A/B\\C?", "A B C")]
    public void Safe_names_work_as_folder_names(string input, string expected) =>
        Assert.Equal(expected, ShareArchiveBuilder.SafeName(input));
}
