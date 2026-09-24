using System.Net;
using System.Net.Http;
using System.Text;
using SteamContentManager.Models;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class CoreTests
{
    [Fact]
    public void DemoDataStore_ContainsSafeLocalData()
    {
        var store = new DemoDataStore();

        Assert.NotEmpty(store.Games);
        Assert.NotEmpty(store.Downloads);
        Assert.Contains(store.Providers, provider => provider.Name == "Steam Web API" && provider.State == ProviderConnectionState.NotConfigured);
        Assert.NotEmpty(store.GenerationTemplates);
    }

    [Fact]
    public async Task DemoDownloadManager_CompletesJobAndReportsProgress()
    {
        var store = new DemoDataStore();
        var logging = new InMemoryLoggingService(store, new NullLocalDatabase());
        var manager = new DemoDownloadManager(logging);
        var job = new DownloadJob { AppId = 730, GameName = "Test", State = DownloadJobState.Queued, TotalSize = "1 GB" };

        await manager.StartAsync(job);

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(100, job.Progress);
        Assert.Contains(store.Logs, log => log.Component == "DownloadManager");
    }

    [Fact]
    public void LoggingService_RedactsSensitiveHeaderNames()
    {
        var store = new DemoDataStore();
        var logging = new InMemoryLoggingService(store, new NullLocalDatabase());

        logging.Add(LogLevel.Debug, "Test", "X-Auth-Key: secret api_key=secret");

        Assert.Contains("[redacted]", store.Logs[0].Message);
        Assert.DoesNotContain("secret", store.Logs[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestService_ValidatesOnlyKnownValidManifest()
    {
        var service = new DemoManifestService();
        var valid = new Manifest { ValidationStatus = "Valid" };
        var pending = new Manifest { ValidationStatus = "Needs validation" };

        Assert.True(await service.ValidateAsync(valid));
        Assert.False(await service.ValidateAsync(pending));
    }

    [Fact]
    public async Task ArtworkService_UsesFallbackWhenArtworkIsUnavailable()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.NotFound));
        using var artwork = new SteamArtworkService(client, Path.Combine(Path.GetTempPath(), "SteamContentManagerTests", Guid.NewGuid().ToString("N")));
        var game = new Game { AppId = 730, Name = "Test game", CoverGlyph = "◆" };

        await artwork.LoadAsync(game);

        Assert.Null(game.ArtworkImage);
        Assert.True(game.IsArtworkFallback);
        Assert.False(game.IsArtworkLoading);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StubHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(string.Empty, Encoding.UTF8)
            });
    }
}
