using SteamContentManager.Models;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class RyuuCatalogTests
{
    private const string SampleJson = """
    [
      { "appid": "10", "name": "Counter-Strike", "type": "game", "nsfw": false, "drm": false,
        "header_image": "https://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg" },
      { "appid": "570", "name": "Dota 2", "type": "game", "header_image": "" },
      { "appid": "abc", "name": "Broken", "type": "game" },
      { "appid": "730", "name": "CS2 Software", "type": "software",
        "header_image": "https://example/730.jpg" }
    ]
    """;

    [Fact]
    public async Task GetGamesAsync_ParsesTheCachedFeedIntoCatalogItems()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scm-ryuu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "games.json"), SampleJson);

        var service = new RyuuCatalogService(new SettingsViewModelTests.StubSettingsService(), cacheDirectory: dir);
        var snapshot = await service.GetGamesAsync();

        Assert.True(snapshot.Succeeded);
        // The "abc" app id is not numeric and is dropped; the three valid ones remain.
        Assert.Equal(3, snapshot.Items.Count);

        var cs = Assert.Single(snapshot.Items, item => item.AppId == 10);
        Assert.Equal("Counter-Strike", cs.Name);
        Assert.Equal(SteamCatalogAppType.Game, cs.AppType);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg", cs.HeaderImageUrl);
        Assert.Contains("library_600x900", cs.PortraitImageUrl);

        // A blank header falls back to the app-id derived Steam header.
        var dota = Assert.Single(snapshot.Items, item => item.AppId == 570);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/570/header.jpg", dota.HeaderImageUrl);

        var software = Assert.Single(snapshot.Items, item => item.AppId == 730);
        Assert.Equal(SteamCatalogAppType.Software, software.AppType);

        Directory.Delete(dir, recursive: true);
    }
}
