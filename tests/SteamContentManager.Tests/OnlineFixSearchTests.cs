using System.Net;
using System.Net.Http;
using System.Text;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class OnlineFixSearchTests
{
    [Fact]
    public void ExtractGameLinks_KeepsOnlyOnlineFixGamePagesAndUnwrapsRedirects()
    {
        var html = "<a href=\"https://online-fix.me/games/elden-ring.html\">Elden Ring</a>"
            + "<a href='/url?q=https%3A%2F%2Fonline-fix.me%2Fgames%2Fcounter-strike-2.html&sa=U&ved=123'>CS2</a>"
            + "<a href=\"https://example.com/games/not-this-one.html\">Other site</a>"
            + "<a href=\"https://online-fix.me/forum/index.php\">Forum</a>"
            + "<a href=\"https://cdn.online-fix.me/games/relative-test.html\">Mirror host</a>";

        var links = OnlineFixSearchService.ExtractGameLinks(html);

        Assert.Equal(
            new[]
            {
                "https://online-fix.me/games/elden-ring.html",
                "https://online-fix.me/games/counter-strike-2.html",
                "https://cdn.online-fix.me/games/relative-test.html"
            },
            links);
    }

    [Theory]
    [InlineData("https://online-fix.me/games/counter-strike-2.html", 100)]
    [InlineData("https://online-fix.me/games/some-other-game.html", 0)]
    // All query words are present, but "global offensive" dilutes precision: sqrt(100 * 50) ≈ 71.
    [InlineData("https://online-fix.me/games/counter-strike-global-offensive.html", 71)]
    [InlineData("https://online-fix.me/games/counter-strike-2.html?utm=x#top", 100)]
    public void ScoreMatch_RanksByTokenCoverage(string url, int expected)
    {
        Assert.Equal(expected, OnlineFixSearchService.ScoreMatch("Counter-Strike 2", url));
    }

    [Fact]
    public void PickBest_ReturnsNullWhenNoLinkMeetsThreshold()
    {
        var links = new[] { "https://online-fix.me/games/unrelated-game.html" };

        var best = new OnlineFixSearchService().PickBest("Counter-Strike 2", links, "test");

        Assert.Null(best);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToSiteWhenNothingMatches()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "<html><a href=\"https://online-fix.me/games/totally-different.html\">Nope</a></html>");
        using var service = new OnlineFixSearchService(new HttpClient(handler));

        var result = await service.SearchAsync("Elden Ring");

        Assert.Equal(OnlineFixSearchService.BaseUrl, result.Url);
        Assert.Equal("Fallback", result.Source);
    }

    [Fact]
    public async Task SearchAndOpenAsync_OpensBestMatchInBrowser()
    {
        var html = "<a href=\"https://online-fix.me/games/elden-ring.html\">Elden Ring online fix</a>";
        using var handler = new StubHandler(HttpStatusCode.OK, html);
        string? opened = null;
        using var service = new OnlineFixSearchService(new HttpClient(handler), url => opened = url);

        var result = await service.SearchAndOpenAsync("Elden Ring");

        Assert.Equal("https://online-fix.me/games/elden-ring.html", result.Url);
        Assert.Equal("online-fix.me search", result.Source);
        Assert.Equal(result.Url, opened);
    }

    [Fact]
    public async Task SearchAsync_EmptyGameNameOpensSiteDirectly()
    {
        string? opened = null;
        using var service = new OnlineFixSearchService(new HttpClient(new StubHandler(HttpStatusCode.OK, string.Empty)), url => opened = url);

        var result = await service.SearchAndOpenAsync("  ");

        Assert.Equal(OnlineFixSearchService.BaseUrl, result.Url);
        Assert.Equal(result.Url, opened);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, "text/html")
            });
    }
}
