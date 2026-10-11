using System.Net;
using Steamy.Services;
using Steamy.Models;

namespace Steamy.Tests;

public sealed class RyuuSecureDownloadTests
{
    [Fact]
    public async Task UsesDocumentedDownloadEndpointAndAuthHeader()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "Steamy-ryuu-tests-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var service = new RyuuSecureDownloadService(new Settings(), client, cacheDirectory);

        var result = await service.DownloadAsync(440, "private-key", "Game");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("https://generator.ryuu.lol/api/download/440", handler.RequestUri?.ToString());
        Assert.Equal("private-key", handler.AuthKey);
        Assert.DoesNotContain("private-key", handler.RequestUri?.ToString());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.ArchivePath));
        Directory.Delete(cacheDirectory, true);
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new();
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthKey = request.Headers.GetValues("X-Auth-Key").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            });
        }
    }
}
