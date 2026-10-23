using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class MetadataBackoffTests
{
    [Fact]
    public async Task UnavailableDetailsAreRetriedAfterShortSessionBackoff()
    {
        using var fixture = new Fixture();
        Assert.Null(await fixture.Service.GetDetailsAsync(480));
        for (var repeat = 0; repeat < 10; repeat++) Assert.Null(await fixture.Service.GetDetailsAsync(480));
        Assert.Equal(1, fixture.Handler.Requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        fixture.Handler.Available = true;
        Assert.NotNull(await fixture.Service.GetDetailsAsync(480));
        Assert.Equal(2, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NetworkAndMalformedResponsesDoNotSuppressTheNextAttempt(bool networkFailure)
    {
        using var fixture = new Fixture();
        fixture.Handler.Offline = networkFailure; fixture.Handler.Malformed = !networkFailure;
        Assert.Null(await fixture.Service.GetDetailsAsync(480));
        fixture.Handler.Offline = fixture.Handler.Malformed = false; fixture.Handler.Available = true;
        Assert.NotNull(await fixture.Service.GetDetailsAsync(480));
        Assert.Equal(2, fixture.Handler.Requests);
    }

    [Fact]
    public async Task CachedMissDoesNotIgnoreCallerCancellation()
    {
        using var fixture = new Fixture();
        Assert.Null(await fixture.Service.GetDetailsAsync(480));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetDetailsAsync(480, cancelled.Token));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReleaseMetadataDoesNotRepeatTheSameBatchUntilItExpires(bool emptyObject)
    {
        using var fixture = new Fixture(); fixture.Handler.EmptyObject = emptyObject;
        var game = new SteamCatalogItem { AppId = 480, Name = "Unavailable public fixture" };
        for (var repeat = 0; repeat < 10; repeat++) await fixture.Service.PrepareReleaseStatusAsync([game]);
        Assert.Equal(1, fixture.Handler.Requests); Assert.False(game.IsReleaseVerified);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1)); fixture.Handler.Available = true;
        await fixture.Service.PrepareReleaseStatusAsync([game]);
        Assert.Equal(2, fixture.Handler.Requests); Assert.True(game.IsReleaseVerified); Assert.False(game.IsUpcoming);
    }

    [Fact]
    public async Task FailedAssetConnectionDoesNotSuppressRetryAndNonSteamIdsDoNotRequestSteam()
    {
        using var fixture = new Fixture();
        await fixture.Service.PrepareReleaseStatusAsync([new() { AppId = -10 }, new() { AppId = 0 }]);
        Assert.Equal(0, fixture.Handler.Requests);
        fixture.Handler.Offline = true;
        await fixture.Service.PrepareReleaseStatusAsync([new() { AppId = 480 }]);
        fixture.Handler.Offline = false; fixture.Handler.Available = true;
        var game = new SteamCatalogItem { AppId = 480 };
        await fixture.Service.PrepareReleaseStatusAsync([game]);
        Assert.Equal(2, fixture.Handler.Requests); Assert.True(game.IsReleaseVerified);
    }

    [Fact]
    public async Task MissesAreBoundedInsteadOfRetainingEveryUnknownAppForTheWholeSession()
    {
        using var fixture = new Fixture();
        for (var id = 1; id <= 300; id++)
        { Assert.Null(await fixture.Service.GetDetailsAsync(id)); fixture.Clock.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.Equal(300, fixture.Handler.Requests);
        Assert.Null(await fixture.Service.GetDetailsAsync(300)); Assert.Equal(300, fixture.Handler.Requests);
        Assert.Null(await fixture.Service.GetDetailsAsync(1)); Assert.Equal(301, fixture.Handler.Requests);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan interval) => _now += interval;
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        internal bool Available, Offline, Malformed, EmptyObject;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new HttpRequestException("Isolated offline fixture.");
            string json;
            if (Malformed) json = "{not-json";
            else if (request.RequestUri!.AbsolutePath.Contains("appdetails"))
            {
                var id = request.RequestUri.Query.Split("appids=")[1].Split('&')[0];
                json = Available ? "{\"" + id + "\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"Available fixture\"}}}" : "{\"" + id + "\":{\"success\":false}}";
            }
            else
            {
                Assert.Contains("IStoreBrowseService/GetItems", request.RequestUri.AbsolutePath);
                json = Available ? "{\"response\":{\"store_items\":[{\"appid\":480,\"success\":1,\"release\":{\"is_coming_soon\":false,\"steam_release_date\":1600000000}}]}}"
                    : EmptyObject ? "{\"response\":{}}" : "{\"response\":{\"store_items\":[]}}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "Steamy-metadata-backoff-" + Guid.NewGuid().ToString("N"));
        internal readonly ManualClock Clock = new();
        internal readonly Handler Handler = new();
        private readonly HttpClient _http;
        internal readonly SteamCatalogService Service;
        internal Fixture() { _http = new(Handler); Service = new(_http, _folder, null, Clock); }
        public void Dispose()
        {
            Service.Dispose(); _http.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
    }
}
