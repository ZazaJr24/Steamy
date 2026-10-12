using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Steamy.Services;
using UCO2.Patcher.Core;

namespace Steamy.UiTests;

public sealed class UcOnlineToolTests
{
    [Fact]
    public async Task InvalidSettingsNeverDownloadATool()
    {
        using var fixture = new Fixture(); var downloader = new PreparationDownloader(fixture.Root);
        var service = new UcOnlineService(downloader, Path.Combine(fixture.Root, "validation-backups"));
        var game = await service.ScanAsync(fixture.Game, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanAsync(game, new PatchOptions { OriginalAppId = 730, InstallPhoton = true }, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanAsync(game, new PatchOptions { OriginalAppId = 0 }, null, default));
        Assert.Equal(0, downloader.Downloads);
    }
    [Fact]
    public async Task UppercaseExeExtensionIsAccepted()
    {
        using var fixture = new Fixture(); var executable = Path.Combine(fixture.Game, "Game.EXE");
        WritePe(executable, 0x8664, 1024);
        var game = await fixture.Service.ScanAsync(fixture.Game, default);
        Assert.Equal(executable, fixture.Service.SelectExecutable(game, executable).ExecutablePath);
    }
    [Fact]
    public async Task CorruptBackupStopsBeforeAnyGameFileChanges()
    {
        using var fixture = new Fixture(); var game = await fixture.Service.ScanAsync(fixture.Game, default);
        var plan = await fixture.Service.PlanAsync(game, new PatchOptions { OriginalAppId = 730, InstallOverlayProxy = false }, null, default);
        await fixture.Service.ApplyAsync(plan, null, default);
        var backup = Assert.Single(await fixture.Service.BackupsAsync(fixture.Game, default));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(backup.ManifestPath))!;
        var original = manifest.Entries.First(entry => entry.BackupRelativePath is not null);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(backup.ManifestPath)!, original.BackupRelativePath!), "Corrupt backup");
        var config = Path.Combine(game.ConfigDirectory, "union-crax.ini"); var before = await File.ReadAllTextAsync(config);
        var api = await File.ReadAllBytesAsync(game.SteamApiPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RestoreAsync(backup.ManifestPath, fixture.Game, null, default));
        Assert.Equal(before, await File.ReadAllTextAsync(config)); Assert.Equal(api, await File.ReadAllBytesAsync(game.SteamApiPath));
    }
    [Fact]
    public async Task CancelDuringSteamLookupIsReported()
    {
        using var fixture = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Steamy.ViewModels.UcOnlineViewModel(fixture.Service, search: async (_, token) =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Array.Empty<SteamSearchResult>(); }, dataRoot: fixture.Root);
        model.Folder = fixture.Game; var task = model.InspectCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); model.CancelCommand.Execute(null); await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Cancelled.", model.Status); Assert.False(model.IsBusy);
    }
    [Fact]
    public void NewFolderClearsMatchAndAllowsReselectingTheSameGame()
    {
        using var fixture = new Fixture(); var model = new Steamy.ViewModels.UcOnlineViewModel(fixture.Service, dataRoot: fixture.Root);
        var match = new SteamSearchResult(730, "Fixture game", null);
        model.SelectedMatch = match; model.NetworkAppId = "440"; model.Options.InstallPhoton = true;
        model.Folder = fixture.Game;
        Assert.Null(model.SelectedMatch); Assert.Equal("480", model.NetworkAppId); Assert.False(model.Options.InstallPhoton);
        model.SelectedMatch = match; Assert.Equal("730", model.AppId);
    }
    [Fact]
    public async Task SetupPreparesSupportedToolsAndReusesThemOnRetry()
    {
        using var fixture = new Fixture(); var root = Path.Combine(fixture.Root, "preparation");
        var downloader = new PreparationDownloader(root); var generator = new PreparationGenerator();
        var report = Path.Combine(root, "status.json");
        await ToolPreparationService.PrepareAsync(report, downloader: downloader, generator: generator, dataRoot: root, bundledRoot: fixture.Root);
        Assert.Equal(7, downloader.Downloads); Assert.Equal(1, generator.Downloads);
        using (var document = JsonDocument.Parse(File.ReadAllText(report)))
        { Assert.True(document.RootElement.GetProperty("Done").GetBoolean()); Assert.Equal(8, document.RootElement.GetProperty("Completed").GetInt32()); }
        Assert.NotNull(FullSteamClient.Find(Path.Combine(root, "gbe_fork", "current")));
        Assert.True(File.Exists(Path.Combine(root, "Tools", "ColdLoaderProxy", "version.dll")));
        await ToolPreparationService.PrepareAsync(report, downloader: downloader, generator: generator, dataRoot: root, bundledRoot: fixture.Root);
        Assert.Equal(7, downloader.Downloads); Assert.Equal(1, generator.Downloads);
    }
    private sealed class PreparationDownloader(string root) : IGitHubToolDownloadService
    {
        private readonly Dictionary<string, string> _cache = [];
        public int Downloads;
        public string? GetCachedPath(GitHubToolDefinition tool) => _cache.GetValueOrDefault(tool.CacheSubfolder);
        public bool HasCached(GitHubToolDefinition tool) => GetCachedPath(tool) is not null;
        public Task<ToolDownloadResult> DownloadLatestAsync(GitHubToolDefinition tool, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Assert.DoesNotContain(tool.RepoName, new[] { "XStoreUnlocker", "Unsteam" });
            Downloads++;
            var path = Path.Combine(root, "ToolCache", tool.CacheSubfolder, tool.TargetExecutableName);
            WritePe(path, 0x8664, tool == ToolDefinitions.Goldberg ? 3 * 1024 * 1024 : 512);
            _cache[tool.CacheSubfolder] = path;
            return Task.FromResult(new ToolDownloadResult(true, "Test file ready", path, "fixture"));
        }
    }
    private sealed class PreparationGenerator : IDenuvoGeneratorDownloadService
    {
        public int Downloads;
        public string? CachedPath => HasCachedExecutable ? "fixture" : null;
        public bool HasCachedExecutable => Downloads > 0;
        public void ClearCache() => Downloads = 0;
        public Task<DenuvoGeneratorDownloadResult> DownloadLatestAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Downloads++; return Task.FromResult(new DenuvoGeneratorDownloadResult(true, "Test generator ready", "fixture", "fixture")); }
    }
    [Fact]
    public async Task InstallsAllApiCopiesAndRestoresVerifiedOriginals()
    {
        using var fixture = new Fixture();
        var rootApi = Path.Combine(fixture.Game, "steam_api64.dll");
        var nestedApi = Path.Combine(fixture.Game, "plugins", "steam_api64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedApi)!);
        var original = File.ReadAllBytes(rootApi); File.WriteAllBytes(nestedApi, original);
        var game = await fixture.Service.ScanAsync(fixture.Game, default);
        var options = new PatchOptions { OriginalAppId = 730, AppId = 480, InstallOverlayProxy = false, QuarantineCompetingFiles = false };
        var plan = await fixture.Service.PlanAsync(game, options, null, default);
        Assert.Equal(2, plan.Operations.Count(operation => operation.Kind == PatchOperationKind.ReplaceFile));
        await fixture.Service.ApplyAsync(plan, null, default);
        Assert.NotEqual(original, File.ReadAllBytes(rootApi));
        Assert.Equal(File.ReadAllBytes(rootApi), File.ReadAllBytes(nestedApi));
        Assert.Contains("ogAppId=730", await File.ReadAllTextAsync(Path.Combine(game.ConfigDirectory, "union-crax.ini")));
        var backup = Assert.Single(await fixture.Service.BackupsAsync(fixture.Game, default));
        Assert.Equal("Complete", backup.Status);
        await fixture.Service.RestoreAsync(backup.ManifestPath, fixture.Game, null, default);
        Assert.Equal(original, File.ReadAllBytes(rootApi)); Assert.Equal(original, File.ReadAllBytes(nestedApi));
        Assert.False(File.Exists(Path.Combine(game.ConfigDirectory, "union-crax.ini")));
    }
    [Fact]
    public async Task RestorePreservesFilesChangedAfterPatching()
    {
        using var fixture = new Fixture(); var game = await fixture.Service.ScanAsync(fixture.Game, default);
        var plan = await fixture.Service.PlanAsync(game, new PatchOptions { OriginalAppId = 730, InstallOverlayProxy = false }, null, default);
        await fixture.Service.ApplyAsync(plan, null, default);
        var backup = Assert.Single(await fixture.Service.BackupsAsync(fixture.Game, default));
        await File.WriteAllTextAsync(game.SteamApiPath, "User's newer file");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(backup.ManifestPath, fixture.Game, null, default));
        Assert.Equal("User's newer file", await File.ReadAllTextAsync(game.SteamApiPath));
    }
    [Fact]
    public async Task MissingBackendSettingsCannotApply()
    {
        using var fixture = new Fixture(); var game = await fixture.Service.ScanAsync(fixture.Game, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PlanAsync(game, new PatchOptions { OriginalAppId = 730, InstallPhoton = true }, null, default));
        Assert.Empty(await fixture.Service.BackupsAsync(fixture.Game, default));
    }
    [Fact]
    public void FullClientRejectsTinyLoaderAndWrongArchitecture()
    {
        using var fixture = new Fixture(); var dir = Path.Combine(fixture.Root, "clients");
        var loader = Path.Combine(dir, "loader", "steamclient64.dll");
        var wrong = Path.Combine(dir, "x86", "steamclient64.dll");
        var full = Path.Combine(dir, "full", "steamclient64.dll");
        WritePe(loader, 0x8664, 1024); WritePe(wrong, 0x14c, 4 * 1024 * 1024); WritePe(full, 0x8664, 3 * 1024 * 1024);
        Assert.False(FullSteamClient.IsFullClient(loader)); Assert.False(FullSteamClient.IsFullClient(wrong)); Assert.Equal(full, FullSteamClient.Find(dir));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolDownloadRejectsTraversalAndWrongChecksum(bool wrongChecksum)
    {
        using var fixture = new Fixture(); using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var output = zip.CreateEntry("x64/steam_api64.dll").Open()) output.Write(new byte[] { 1, 2, 3 });
            if (!wrongChecksum) { using var output = zip.CreateEntry("../outside.dll").Open(); output.Write(new byte[] { 1 }); }
        }
        var bytes = memory.ToArray(); using var http = new HttpClient(new PackageHandler(bytes, wrongChecksum));
        using var downloader = new GitHubToolDownloadService(http, Path.Combine(fixture.Root, "download"));
        var result = await downloader.DownloadLatestAsync(ToolDefinitions.UcOnline);
        Assert.False(result.Succeeded); Assert.False(File.Exists(Path.Combine(fixture.Root, "download", "outside.dll")));
        Assert.False(downloader.HasCached(ToolDefinitions.UcOnline));
    }
    [Fact]
    public async Task CompleteToolPackageReplacesCacheTogetherWithItsPlugins()
    {
        using var fixture = new Fixture(); var cache = Path.Combine(fixture.Root, "download");
        var old = Path.Combine(cache, "UcOnline", "x64", "steam_api64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.WriteAllText(old, "Old cached DLL");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var output = zip.CreateEntry("package/x64/steam_api64.dll").Open()) output.Write(new byte[] { 1, 2, 3 });
            using (var output = zip.CreateEntry("package/plugins/photon_universal.dll").Open()) output.Write(new byte[] { 4, 5, 6 });
        }
        using var http = new HttpClient(new PackageHandler(memory.ToArray(), false));
        using var downloader = new GitHubToolDownloadService(http, cache);
        var result = await downloader.DownloadLatestAsync(ToolDefinitions.UcOnline);
        Assert.True(result.Succeeded, result.Message); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(result.CachedPath!));
        Assert.True(File.Exists(Path.Combine(cache, "UcOnline", "package", "plugins", "photon_universal.dll")));
        Assert.Single(Directory.EnumerateDirectories(cache)); Assert.False(File.Exists(old));
    }
    private sealed class PackageHandler(byte[] bytes, bool wrongChecksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var json = JsonSerializer.Serialize(new { tag_name = "v1.0.0", assets = new[] { new { name = "uc-online2-release.zip", browser_download_url = "https://github.com/LukeWarmSodas/uc-online2/releases/download/v1.0.0/uc-online2-release.zip", digest = "sha256:" + (wrongChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes))) } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Steamy-UcOnline-tests-" + Guid.NewGuid().ToString("N"));
        public string Game => Path.Combine(Root, "Game");
        public UcOnlineService Service { get; }
        private readonly GitHubToolDownloadService _downloads;
        public Fixture()
        {
            WritePe(Path.Combine(Game, "game.exe"), 0x8664, 1024);
            WritePe(Path.Combine(Game, "steam_api64.dll"), 0x8664, 300);
            var cache = Path.Combine(Root, "cache");
            WritePe(Path.Combine(cache, "UcOnline", "package", "x64", "steam_api64.dll"), 0x8664, 512);
            _downloads = new GitHubToolDownloadService(new HttpClient(new PackageHandler([], false)), cache);
            Service = new UcOnlineService(_downloads, Path.Combine(Root, "backups"));
        }
        public void Dispose() { _downloads.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static void WritePe(string file, ushort machine, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file); stream.SetLength(size); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5A4D); stream.Position = 0x3c; writer.Write(64); stream.Position = 64; writer.Write((uint)0x4550); writer.Write(machine);
    }
}
