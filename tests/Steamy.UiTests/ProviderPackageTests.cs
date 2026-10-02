using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

// Controlled transport fixtures exercise the production parser; these are not live API checks.
public sealed class ProviderPackageTests
{
    private const string Lua = "addappid(480)\naddappid(481,0,\"aabbccddeeff0011\")\nif setManifestid then\nsetManifestid(481,18446744073709551615)\nend";
    [Theory]
    [InlineData(ManifestSource.Hubcap, "zip")]
    [InlineData(ManifestSource.Hubcap, "html-then-zip")]
    [InlineData(ManifestSource.Hubcap, "invalid-utf8-then-zip")]
    [InlineData(ManifestSource.Hubcap, "lua-only")]
    [InlineData(ManifestSource.DepotBox, "zip")]
    [InlineData(ManifestSource.DepotBox, "lua-only")]
    [InlineData(ManifestSource.DepotBox, "corrupt-then-lua")]
    public async Task ValidPackagesAreCanonicalAndIsolated(ManifestSource source, string scenario)
    {
        using var fixture = new Fixture(source, scenario);
        var result = await fixture.Service.DownloadManifestsAsync(source, 480);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("setManifestid(481,\"18446744073709551615\")", result.LuaContent);
        Assert.DoesNotContain("if ", result.LuaContent);
        Assert.Equal(result.LuaContent, File.ReadAllText(Path.Combine(result.WorkDirectory!, "480.lua")));
        Assert.False(File.Exists(Path.Combine(result.WorkDirectory!, "untrusted.exe")));
    }
    [Theory]
    [InlineData(ManifestSource.Hubcap, "html")]
    [InlineData(ManifestSource.Hubcap, "invalid-utf8")]
    [InlineData(ManifestSource.Hubcap, "wrong-app")]
    [InlineData(ManifestSource.Hubcap, "corrupt")]
    [InlineData(ManifestSource.Hubcap, "mismatched-manifest")]
    [InlineData(ManifestSource.DepotBox, "html")]
    [InlineData(ManifestSource.DepotBox, "invalid-utf8")]
    [InlineData(ManifestSource.DepotBox, "wrong-app")]
    [InlineData(ManifestSource.DepotBox, "corrupt")]
    [InlineData(ManifestSource.DepotBox, "mismatched-manifest")]
    public async Task BadPackagesLeaveNoPublishedFiles(ManifestSource source, string scenario)
    {
        using var fixture = new Fixture(source, scenario);
        var result = await fixture.Service.DownloadManifestsAsync(source, 480);
        Assert.False(result.Succeeded);
        Assert.Null(result.WorkDirectory);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "pack-*", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(503)]
    public async Task HttpErrorsNeverBecomeUsableMetadata(int status)
    {
        using var fixture = new Fixture(ManifestSource.DepotBox, "http", status);
        var result = await fixture.Service.DownloadManifestsAsync(ManifestSource.DepotBox, 480);
        Assert.False(result.Succeeded);
        Assert.Contains($"HTTP {status}", result.Message);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(ManifestSource.Hubcap)] [InlineData(ManifestSource.DepotBox)]
    public async Task CancellationCleansStagingAndConcurrentPackagesDoNotShareFiles(ManifestSource source)
    {
        using var fixture = new Fixture(source, "zip");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Service.DownloadManifestsAsync(source, 480)));
        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
        Assert.Equal(4, results.Select(result => result.WorkDirectory).Distinct().Count());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.DownloadManifestsAsync(source, 480, cancellationToken: cancelled.Token));
        Assert.Equal(4, Directory.EnumerateDirectories(fixture.Root, "pack-*", SearchOption.AllDirectories).Count());
    }
    [Theory]
    [InlineData(ManifestSource.Hubcap)] [InlineData(ManifestSource.DepotBox)]
    public async Task CancellationDuringResponseCleansTheCreatedPackage(ManifestSource source)
    {
        using var fixture = new Fixture(source, "cancel");
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.DownloadManifestsAsync(source, 480, cancellationToken: cancellation.Token);
        await fixture.Transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.EnumerateDirectories(fixture.Root, "pack-*", SearchOption.AllDirectories));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "pack-*", SearchOption.AllDirectories));
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Steamy-provider-" + Guid.NewGuid().ToString("N"));
        public ManifestSourceService Service { get; }
        public Transport Transport { get; }
        private readonly HttpClient _http;
        public Fixture(ManifestSource source, string scenario, int status = 200)
        {
            Directory.CreateDirectory(Root);
            Transport = new Transport(source, scenario, status);
            _http = new HttpClient(Transport);
            Service = new(new Settings(), (ISecureCredentialService)OfflineServiceProxy.Create(typeof(ISecureCredentialService)),
                (IRyuuSecureDownloadService)OfflineServiceProxy.Create(typeof(IRyuuSecureDownloadService)),
                (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService)), _http, Root);
        }
        public void Dispose() { Service.Dispose(); _http.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new() { HubcapApiKey = "fixture-key", DepotBoxApiKey = "fixture-key" };
        public Task SaveAsync(AppSettings value, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Transport(ManifestSource source, string scenario, int status) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static async Task<HttpResponseMessage> WaitForCancellation(CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Started.TrySetResult();
            if (scenario == "cancel") return WaitForCancellation(token);
            Assert.Equal(source == ManifestSource.Hubcap ? "hubcapmanifest.com" : "depotbox.org", request.RequestUri!.Host);
            Assert.Equal("fixture-key", request.Headers.GetValues("X-API-Key").Single());
            var luaEndpoint = request.RequestUri.AbsolutePath.Contains("lua", StringComparison.Ordinal);
            if (scenario == "http" || scenario == "lua-only" && !luaEndpoint)
                return Task.FromResult(new HttpResponseMessage(scenario == "http" ? (HttpStatusCode)status : HttpStatusCode.NotFound));
            byte[] bytes;
            if (scenario == "corrupt-then-lua" && !luaEndpoint || scenario == "corrupt") bytes = [80,75,3,4,0];
            else if (scenario.StartsWith("html") && (scenario == "html" || luaEndpoint)) bytes = Encoding.UTF8.GetBytes("<html>Login required</html>");
            else if (scenario.StartsWith("invalid-utf8") && (scenario == "invalid-utf8" || luaEndpoint)) bytes = [0xC3,0x28];
            else if (luaEndpoint) bytes = Encoding.UTF8.GetBytes(scenario == "wrong-app" ? "addappid(999)" : Lua);
            else
            {
                using var output = new MemoryStream();
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    using (var writer = new StreamWriter(archive.CreateEntry("480.lua").Open())) writer.Write(scenario == "wrong-app" ? "addappid(999)" : Lua);
                    using (var writer = new StreamWriter(archive.CreateEntry(scenario == "mismatched-manifest" ? "999_123.manifest" : "481_18446744073709551615.manifest").Open())) writer.Write("fixture manifest");
                }
                bytes = output.ToArray();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
