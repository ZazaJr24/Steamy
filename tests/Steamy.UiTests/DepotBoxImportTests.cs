using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DepotBoxImportTests
{
    private const string Lua = """
        -- Provider metadata fixture, never evaluated.
        addappid(480)
        addappid(481,0,"aabbccddeeff0011")
        if setManifestid then
          setManifestid(481,18446744073709551615,1024)
        end
        """;

    [Theory]
    [InlineData("zip")]
    [InlineData("octet-lua")]
    [InlineData("fallback-lua")]
    public async Task DepotBoxSourceAddsCanonicalMetadataToVerifiedBackend(string response)
    {
        using var fixture = new Fixture(response);
        var result = await fixture.Backend.AddFromSourceAsync(fixture.Root, 480, ManifestSource.DepotBox);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(480, Assert.Single(result.AppIds));
        var installed = File.ReadAllText(Path.Combine(fixture.Root, "config/stplug-in/480.lua"));
        Assert.Contains("setManifestid(481,\"18446744073709551615\",1024)", installed);
        Assert.DoesNotContain("if ", installed);
        Assert.DoesNotContain("os.", installed);
        Assert.Equal(response == "zip" ? 1 : 0, result.ManifestCount);
        if (response == "zip")
            Assert.Equal("depot fixture", File.ReadAllText(Path.Combine(fixture.Root, "depotcache/481_18446744073709551615.manifest")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "untrusted.exe")));
        Assert.Equal(response == "fallback-lua" ? 3 : 2, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData("html", "web page")]
    [InlineData("unauthorized", "HTTP 401")]
    [InlineData("unsafe-lua", "unsupported Lua")]
    public async Task FailedOrUnsafeProviderResponseNeverAddsAGame(string response, string message)
    {
        using var fixture = new Fixture(response);
        var result = await fixture.Backend.AddFromSourceAsync(fixture.Root, 480, ManifestSource.DepotBox);
        Assert.False(result.Succeeded);
        Assert.Contains(message, result.Message);
        Assert.Empty(result.AppIds);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "config/stplug-in/480.lua")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "depotcache")));
        Assert.True(fixture.Backend.Detect(fixture.Root).BackendInstalled);
    }

    [Fact]
    public async Task MissingApiKeyStopsBeforeAnyProviderRequest()
    {
        using var fixture = new Fixture("zip", key: "");
        var result = await fixture.Backend.AddFromSourceAsync(fixture.Root, 480, ManifestSource.DepotBox);
        Assert.False(result.Succeeded);
        Assert.Contains("No DepotBox API key", result.Message);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "config/stplug-in/480.lua")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "steamy-depotbox-" + Guid.NewGuid().ToString("N"));
        public Transport Transport { get; }
        public BetterSteamToolsService Backend { get; }
        private readonly HttpClient _http;
        private readonly ManifestSourceService _sources;
        public Fixture(string response, string key = "test-key")
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "steam.exe"), "Offline test fixture, never executed.");
            var files = SteamToolsBackend.Official104.Keys.ToDictionary(name => name, name => Encoding.UTF8.GetBytes("MZfixture-" + name));
            foreach (var file in files) File.WriteAllBytes(Path.Combine(Root, file.Key), file.Value);
            File.WriteAllBytes(Path.Combine(Root, SteamToolsBackend.ReceiptPath), SteamToolsBackend.CreateReceipt("test", files));
            var settings = new Settings(key);
            Transport = new Transport(response);
            _http = new HttpClient(Transport);
            _sources = new ManifestSourceService(settings,
                (ISecureCredentialService)OfflineServiceProxy.Create(typeof(ISecureCredentialService)),
                (IRyuuSecureDownloadService)OfflineServiceProxy.Create(typeof(IRyuuSecureDownloadService)),
                (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService)), _http, Path.Combine(Root, "source-work"));
            Backend = new BetterSteamToolsService(settings, _sources, _http, () => false);
            Assert.True(Backend.Detect(Root).BackendInstalled);
        }
        public void Dispose() { _sources.Dispose(); _http.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Settings(string key) : ISettingsService
    {
        public AppSettings Load() => new() { DepotBoxApiKey = key };
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class Transport(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("depotbox.org", request.RequestUri!.Host);
            Assert.Equal("test-key", request.Headers.GetValues("X-API-Key").Single());
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("/availability"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"available\":true}") });
            if (response == "unauthorized" || response == "fallback-lua" && request.RequestUri.AbsolutePath == "/api/direct-download")
                return Task.FromResult(new HttpResponseMessage(response == "unauthorized" ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable));
            byte[] bytes;
            if (response == "zip")
            {
                using var stream = new MemoryStream();
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var (name, content) in new[] { ("nested/480.lua", Lua), ("nested/481_18446744073709551615.manifest", "depot fixture"), ("untrusted.exe", "Never installed.") })
                    { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
                }
                bytes = stream.ToArray();
            }
            else bytes = Encoding.UTF8.GetBytes(response == "html" ? "<html>Browser verification required</html>"
                : response == "unsafe-lua" ? Lua + "\nos.execute('never run')" : Lua);
            var payload = new ByteArrayContent(bytes);
            payload.Headers.ContentType = new MediaTypeHeaderValue(response == "html" ? "text/html" : "application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = payload });
        }
    }
}
