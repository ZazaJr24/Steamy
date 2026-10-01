using System.IO;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class BetterSteamToolsServiceTests
{
    [Fact]
    public async Task AutomaticSourceDetectionFallsBackAndInstallsOnlyRealReturnedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-bst-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var file in new[] { "steam.exe", "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" }) File.WriteAllText(Path.Combine(root,file),"fixture only; never executed");
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
