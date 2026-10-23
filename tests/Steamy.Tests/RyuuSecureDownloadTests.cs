using System.Net;
using System.IO.Compression;
using Steamy.Services;
using Steamy.Models;

namespace Steamy.Tests;

public sealed class RyuuSecureDownloadTests
{
    [Theory]
    [InlineData("private-key")]
    [InlineData("")]
    public async Task UsesRelayByDefaultAndPersonalKeyOnlyOnProvider(string key)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "Steamy-ryuu-tests-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var service = new RyuuSecureDownloadService(new Settings(), client, cacheDirectory);

        var result = await service.DownloadAsync(440, key, "Game");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(key.Length == 0 ? ProviderRelay.Url("ryuu", 440, "archive") : "https://generator.ryuu.lol/api/download/440", handler.RequestUri?.ToString());
        Assert.Equal(key.Length == 0 ? null : key, handler.AuthKey);
        Assert.DoesNotContain("private-key", handler.RequestUri?.ToString());
        Assert.Equal(handler.Payload, await File.ReadAllBytesAsync(result.ArchivePath));
        Assert.Empty(Directory.GetFiles(cacheDirectory, "*.partial"));
        Directory.Delete(cacheDirectory, true);
    }

    [Fact]
    public async Task ConcurrentRequestsReuseOneVerifiedArchiveAndInvalidCacheIsReplaced()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-ryuu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "440.zip"), "incomplete previous transfer");
        try
        {
            using var handler = new Handler(); using var client = new HttpClient(handler);
            using var service = new RyuuSecureDownloadService(new Settings(), client, directory);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.DownloadAsync(440, "", "Game")));
            Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
            Assert.Equal(1, handler.Calls);
            File.SetLastWriteTimeUtc(results[0].ArchivePath, DateTime.UtcNow.AddHours(-2));
            Assert.True((await service.DownloadAsync(440, "", "Game")).Succeeded);
            Assert.Equal(2, handler.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidResponsesCannotReplacePreviousArchive(bool oversized)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-ryuu-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler(); using var client = new HttpClient(handler);
            using var service = new RyuuSecureDownloadService(new Settings(), client, directory);
            var previous = await service.DownloadAsync(440, "", "Game"); Assert.True(previous.Succeeded);
            var bytes = await File.ReadAllBytesAsync(previous.ArchivePath);
            File.SetLastWriteTimeUtc(previous.ArchivePath, DateTime.UtcNow.AddHours(-2));
            handler.Payload = "<html>invalid</html>"u8.ToArray(); handler.Oversized = oversized;
            Assert.False((await service.DownloadAsync(440, "", "Game")).Succeeded);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(previous.ArchivePath));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledAndInvalidRequestsMakeNoNetworkCall()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-ryuu-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler(); using var client = new HttpClient(handler);
            using var service = new RyuuSecureDownloadService(new Settings(), client, directory);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.False((await service.DownloadAsync(440, "", "Game", cancellationToken: cancellation.Token)).Succeeded);
            Assert.False((await service.DownloadAsync(0, "", "Game")).Succeeded);
            Assert.Equal(0, handler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
        public int Calls;
        public bool Oversized;
        public byte[] Payload = CreateZip();

        private static byte[] CreateZip()
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(archive.CreateEntry("440.lua").Open())) writer.Write("addappid(440)");
            return buffer.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Calls++;
            AuthKey = request.Headers.TryGetValues("X-Auth-Key", out var values) ? values.Single() : null;
            var content = new ByteArrayContent(Payload);
            if (Oversized) content.Headers.ContentLength = 129L * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }
}
