using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class BetterSteamToolsServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationUsesVerifiedBytesAndNeverReportsDamagedFilesAsInstalled(bool wrongDigest)
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-bst-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "steam.exe"), "offline fixture; never executed");
            var bytes = BackendArchive();
            using var http = new HttpClient(new BackendHttp(bytes, wrongDigest));
            var service = new BetterSteamToolsService(new Settings(), new Source(root), http, () => false);
            Assert.False(service.Detect(root).BackendInstalled);
            var result = await service.InstallBackendAsync(root);
            Assert.Equal(!wrongDigest, result.Succeeded);
            Assert.Equal(!wrongDigest, service.Detect(root).BackendInstalled);
            if (wrongDigest)
            {
                Assert.Contains("SHA-256", result.Message);
                Assert.False(File.Exists(Path.Combine(root, "OpenSteamTool.dll")));
                Assert.False(File.Exists(Path.Combine(root, SteamToolsBackend.ReceiptPath)));
            }
            else
            {
                Assert.Contains("config/stplug-in", File.ReadAllText(Path.Combine(root, "opensteamtool.toml")));
                File.WriteAllText(Path.Combine(root, "OpenSteamTool.dll"), "MZcorrupt or unrelated backend");
                Assert.False(service.Detect(root).BackendInstalled);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GitHubRateLimitsUseTheOfficialManifestAndStillRequireThePinnedArchiveChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-bst-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "steam.exe"), "offline fixture; never executed");
            using var http = new HttpClient(new BackendHttp(BackendArchive(), false, rateLimited: true));
            var service = new BetterSteamToolsService(new Settings(), new Source(root), http, () => false);
            var result = await service.InstallBackendAsync(root);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAMY_BST_TEST_ARCHIVE")))
            {
                Assert.False(result.Succeeded);
                Assert.Contains("SHA-256", result.Message);
                Assert.False(service.Detect(root).BackendInstalled);
            }
            else
            {
                Assert.True(result.Succeeded, result.Message);
                Assert.True(service.Detect(root).BackendInstalled);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RunningSteamPreventsInstallationBeforeNetworkOrDiskChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-bst-running-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "steam.exe"), "offline fixture; never executed");
            var handler = new BackendHttp(BackendArchive(), false);
            using var http = new HttpClient(handler);
            var service = new BetterSteamToolsService(new Settings(), new Source(root), http, () => true);
            var result = await service.InstallBackendAsync(root);
            Assert.False(result.Succeeded);
            Assert.Contains("Close Steam", result.Message);
            Assert.Equal(0, handler.Requests);
            Assert.Equal("steam.exe", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] BackendArchive()
    {
        var real = Environment.GetEnvironmentVariable("STEAMY_BST_TEST_ARCHIVE");
        if (!string.IsNullOrEmpty(real)) return File.ReadAllBytes(real);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var name in SteamToolsBackend.Official104.Keys)
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("MZoffline fixture; never executed"); }
        return bytes.ToArray();
    }

    private sealed class BackendHttp(byte[] archive, bool wrongDigest, bool rateLimited = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var url = request.RequestUri!.AbsoluteUri;
            if (rateLimited && url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("version = \"v1.0.4\"\nsha256 = \"" + SteamToolsBackend.Official104["OpenSteamTool.dll"] + "\"\n") });
            HttpContent content = url == "https://api.github.com/repos/madoiscool/BetterSteamTools/releases/latest"
                ? new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = "v1.0.4", assets = new[] { new { name = "OpenSteamTool-v1.0.4-Release.zip",
                        digest = "sha256:" + (wrongDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()),
                        browser_download_url = "https://github.com/madoiscool/BetterSteamTools/releases/download/v1.0.4/OpenSteamTool-v1.0.4-Release.zip" } }
                }))
                : new ByteArrayContent(archive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task AutomaticSourceDetectionFallsBackAndInstallsOnlyRealReturnedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-bst-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "steam.exe"), "fixture only; never executed");
            var payload = SteamToolsBackend.Official104.Keys.ToDictionary(name => name, _ => System.Text.Encoding.UTF8.GetBytes("MZoffline test fixture; never executed"));
            foreach (var item in payload) File.WriteAllBytes(Path.Combine(root, item.Key), item.Value);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllBytes(Path.Combine(root, SteamToolsBackend.ReceiptPath), SteamToolsBackend.CreateReceipt("test", payload));
            var source = new Source(root);
            var service = new BetterSteamToolsService(new Settings(), source);
            var result = await service.AddFromSourceAsync(root, 480, null);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal([ManifestSource.Sushi, ManifestSource.Zaza], source.Checked);
            Assert.Equal(480, Assert.Single(result.AppIds));
            Assert.Contains("addappid(480)", File.ReadAllText(Path.Combine(root,"config/stplug-in/480.lua")));
            Assert.Equal("source manifest", File.ReadAllText(Path.Combine(root,"depotcache/481_123.manifest")));
            Assert.False(Directory.Exists(Path.Combine(root,"config/depotcache")));
            Assert.Contains("config/stplug-in", File.ReadAllText(Path.Combine(root,"opensteamtool.toml")));
            var previous = File.ReadAllText(Path.Combine(root,"config/stplug-in/480.lua"));
            result = await service.AddFromSourceAsync(root, 999, ManifestSource.Hubcap);
            Assert.False(result.Succeeded);
            Assert.Equal(previous, File.ReadAllText(Path.Combine(root,"config/stplug-in/480.lua")));
            Assert.False(File.Exists(Path.Combine(root,"config/stplug-in/999.lua")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new();
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Source(string root) : IManifestSourceService
    {
        public List<ManifestSource> Checked { get; } = [];
        public IReadOnlyList<ManifestSourceInfo> Sources => [new(ManifestSource.Sushi,"Sushi","","",false),new(ManifestSource.Zaza,"Zaza","","",false),new(ManifestSource.Hubcap,"Hubcap","","",true)];
        public Task<ManifestAvailability> CheckAvailabilityAsync(ManifestSource source, int appId, CancellationToken token = default)
        { Checked.Add(source); return Task.FromResult(new ManifestAvailability(source == ManifestSource.Zaza,true,source == ManifestSource.Hubcap ? "No API key configured" : "Availability checked")); }
        public Task<ManifestDownloadResult> DownloadManifestsAsync(ManifestSource source, int appId, IProgress<string>? progress = null, CancellationToken token = default)
        {
            var folder = Path.Combine(root,"provider-work"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"481_123.manifest"),"source manifest");
            return Task.FromResult(new ManifestDownloadResult(true,"Downloaded","addappid(480)\naddappid(481,0,\"aabbccddeeff0011\")\nsetManifestid(481,\"123\")",folder));
        }
    }
}
