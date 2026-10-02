using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class UpdateServiceTests
{
    private static string Description(string digest = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") =>
        $$"""{"tag_name":"v0.4.16","draft":false,"prerelease":false,"published_at":"2026-10-02T15:02:00Z","assets":[{"name":"Steamy-v0.4.16.zip","browser_download_url":"https://github.com/ZazaJr24/Steamy/releases/download/v0.4.16/Steamy-v0.4.16.zip","size":100,"digest":"{{digest}}"}]}""";

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task BlockedApiUsesPublishedManifestWithoutCredentials(int status)
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com"
            ? new((HttpStatusCode)status) : Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        var update = await service.CheckAsync();
        Assert.Equal(new Version(0, 4, 16), update!.Version);
        Assert.Equal(64, update.Sha256!.Length);
        Assert.Equal(GitHubUpdateService.PublishedManifestUri, handler.Requests[1]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ApiConnectionFailureStillFindsThePublishedUpdate()
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? throw new HttpRequestException("Offline API") : Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        Assert.NotNull(await service.CheckAsync());
    }

    [Fact]
    public async Task SuccessfulApiRetainsChecksumAndDoesNotRequestFallback()
    {
        using var handler = new Handler((_, _) => Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        Assert.NotNull((await service.CheckAsync())!.Sha256);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("external")]
    [InlineData("size")]
    [InlineData("published")]
    public async Task InvalidFallbackNeverReportsUpToDate(string failure)
    {
        var data = JsonNode.Parse(Description())!;
        var asset = data["assets"]![0]!;
        switch (failure)
        {
            case "digest": asset["digest"] = "sha256:invalid"; break;
            case "draft": data["draft"] = true; break;
            case "prerelease": data["prerelease"] = true; break;
            case "external": asset["browser_download_url"] = "https://example.invalid/Steamy-v0.4.16.zip"; break;
            case "size": asset["size"] = 0; break;
            case "published": data["published_at"] = "unknown"; break;
        }
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? new(HttpStatusCode.Forbidden) : Json(data.ToJsonString()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 16));
        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task BothSourcesFailWithAnActionableErrorInsteadOfFalseSuccess()
    {
        using var handler = new Handler((_, _) => new(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
        Assert.Contains("releases/latest", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancelledCheckDoesNotStartFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Json(Description()); });
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedFallbackIsRejected()
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? new(HttpStatusCode.Forbidden) : Json(new string(' ', 256 * 1024 + 1)));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http);
        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadChecksumIsCheckedBeforeReplacingAnyInstalledFile(bool correct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-update-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(folder, "app");
        var work = Path.Combine(folder, "work");
        Directory.CreateDirectory(install);
        var original = Path.Combine(install, "Steamy.exe");
        await File.WriteAllTextAsync(original, "original executable");
        try
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("Steamy.exe").Open())) writer.Write("new executable");
            var bytes = archive.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            using var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var http = new HttpClient(handler);
            using var service = new GitHubUpdateService(http, install, work);
            var update = new UpdateInfo(new(0, 4, 16), "v0.4.16", new("https://github.com/ZazaJr24/Steamy/releases/download/v0.4.16/Steamy-v0.4.16.zip"), bytes.Length,
                correct ? checksum : new string('0', 64));
            if (correct)
            {
                await service.InstallAsync(update);
                Assert.Equal("new executable", await File.ReadAllTextAsync(original));
            }
            else
            {
                await Assert.ThrowsAsync<UpdateException>(() => service.InstallAsync(update));
                Assert.Equal("original executable", await File.ReadAllTextAsync(original));
                Assert.False(Directory.Exists(Path.Combine(work, "staging")));
            }
            Assert.False(File.Exists(Path.Combine(work, "Steamy-v0.4.16.zip")));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<Uri, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!, cancellationToken));
        }
    }
}
