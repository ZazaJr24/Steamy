using Steamy.Services;

namespace Steamy.Tests;

public class OwnedGamesTests
{
    [Fact]
    public void Gameslist_xml_is_parsed_into_games()
    {
        const string xml = """
            <gamesList>
              <games>
                <game>
                  <appID>570</appID>
                  <name>Dota 2</name>
                </game>
                <game>
                  <appID>730</appID>
                  <name>Counter-Strike 2</name>
                  <hoursOnRecord>1.2</hoursOnRecord>
                </game>
                <game>
                  <appID>0</appID>
                  <name>broken entry is skipped</name>
                </game>
              </games>
            </gamesList>
            """;

        var games = OwnedGamesXml.Parse(xml);

        Assert.Equal(2, games.Count);
        Assert.Equal(new[] { 570, 730 }, games.Select(game => game.AppId));
        Assert.Equal("Dota 2", games[0].Name);
        Assert.All(games, game => Assert.False(game.Installed));
    }

    [Fact]
    public void Malformed_xml_yields_no_games()
    {
        Assert.Empty(OwnedGamesXml.Parse("<gamesList><games>"));
        Assert.Empty(OwnedGamesXml.Parse(string.Empty));
        Assert.Empty(OwnedGamesXml.Parse("not xml at all"));
    }

    [Fact]
    public void Owned_games_are_merged_behind_scanned_ones()
    {
        var installed = new ShareCandidate(730, "Counter-Strike 2", ShareSourceKind.SteamLibrary, "steamapps",
            new[] { new ShareFile("a", "731_1.manifest", 10) }, Array.Empty<DepotManifestRef>(), DateTime.UtcNow);
        var scanned = new[] { installed };
        var owned = new[] { new OwnedGame(730, "Counter-Strike 2", Installed: true), new OwnedGame(570, "Dota 2", Installed: false) };

        var merged = OwnedGamesMerge.MergeOwned(scanned, owned);

        Assert.Equal(2, merged.Count);
        // The scanned entry wins: it carries the local files.
        Assert.Equal(ShareSourceKind.SteamLibrary, merged.Single(candidate => candidate.AppId == 730).Source);
        // The not-installed owned game becomes an auto-fetch candidate with no local files.
        var dota = merged.Single(candidate => candidate.AppId == 570);
        Assert.Empty(dota.Files);
        Assert.True(dota.NeedsManifestFetch);
        Assert.False(installed.NeedsManifestFetch);
    }

    [Fact]
    public void Ignored_runtime_ids_are_not_merged()
    {
        var owned = new[] { new OwnedGame(228980, "Steamworks Common Redistributables", Installed: true) };

        var merged = OwnedGamesMerge.MergeOwned(Array.Empty<ShareCandidate>(), owned);

        Assert.Empty(merged);
    }
}
