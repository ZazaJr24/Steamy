using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Collections.Concurrent;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class PreparedDownloadServiceTests
{
    [Fact]
    public async Task ChangedResumeManifestFailsBeforeFetchingAToolAndKeepsGameFiles()
    {
        using var fixture = new Fixture();
        var plan = (await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi)).Plan!;
        await fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        File.WriteAllText(Path.Combine(session,"101_111.manifest"),"corrupted saved manifest");
        Directory.CreateDirectory(fixture.Target);
        var existing = Path.Combine(fixture.Target,"game.bin");
        File.WriteAllText(existing,"keep game files");
        var requestsBeforeResume = fixture.Http.Calls;
        var resumed = await fixture.Service.ResumeDownloadAsync(100,fixture.Target);
        Assert.False(resumed.Succeeded);
        Assert.Contains("missing or invalid",resumed.Message);
        Assert.Equal(requestsBeforeResume,fixture.Http.Calls);
        Assert.Equal("keep game files",File.ReadAllText(existing));
    }

    [Fact]
    public async Task LocalSelectionAndResumeSurviveDeletionOfOriginalZip()
    {
        using var fixture = new Fixture();
        var package = Path.Combine(fixture.Work, "local.zip");
        Directory.CreateDirectory(fixture.Work);
        using (var zip = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("100.lua").Open()))
                writer.Write("addappid(100)\naddappid(101,1,\"aabbccddeeff0011\")\nsetManifestid(101,\"111\")");
            using (var writer = new StreamWriter(zip.CreateEntry("101_111.manifest").Open())) writer.Write("local pinned manifest");
        }
        var preparation = await fixture.Service.PrepareLocalPackageAsync(100, package);
        Assert.True(preparation.Succeeded, preparation.Message);
        var plan = preparation.Plan!;
        Assert.Equal(ManifestSource.Local, plan.Source);
        File.Delete(package);
        await fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);
        fixture.Service.DiscardPreparedDownload(plan.Id);
        await fixture.Service.ResumeDownloadAsync(100, fixture.Target);
        Assert.Equal(0, fixture.Source.Calls);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal("local pinned manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.Equal(new CachedDepotManifest(101,"111"),Assert.Single(DepotResumeStateStore.Read(session,100,fixture.Target)!.Depots));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Work,"prepared")));
    }

    [Fact]
    public async Task StartUsesThePreparedSourceSnapshotAndPinsOnlyTheSelectedVersion()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi);
        var plan = Assert.IsType<PreparedGameDownload>(prepared.Plan);
        File.WriteAllText(Path.Combine(fixture.Source.Directory, "101_111.manifest"), "source changed after selection");

        var result = await fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);

        Assert.False(result.Succeeded); // The offline fixture has no executable; it never launches a tool.
        Assert.Equal(1, fixture.Source.Calls);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        Assert.Equal("original older manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.False(File.Exists(Path.Combine(session, "102_222.manifest")));
        Assert.Equal("101;aabbccddeeff0011", File.ReadAllText(Path.Combine(session, "100.key")).Trim());
        Assert.DoesNotContain("aabbccddeeff0011", File.ReadAllText(Path.Combine(session, "resume.json")));
    }

    [Fact]
    public async Task SteamDisplayInformationCannotChangeTheSourceSelectionOrRefreshItDuringResume()
    {
        var metadata = new MetadataFixture();
        using var fixture = new Fixture(metadata);
        var preparation = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi);
        var plan = Assert.IsType<PreparedGameDownload>(preparation.Plan);
        Assert.Equal(1, metadata.Calls);
        Assert.Equal([101, 102], plan.Depots.Select(depot => depot.DepotId).ToArray());
        var enriched = plan.Depots.Single(depot => depot.DepotId == 101);
        Assert.Equal("Windows game content", enriched.Name);
        Assert.Equal("999", enriched.DefaultManifestId);
        var selected = enriched.Versions.Single(version => version.ManifestId == "111");
        Assert.Equal(512L, selected.SizeBytes);
        Assert.Equal("Build 42", selected.BuildLabel);
        Assert.Equal("public", selected.BranchName);
        var unmatched = enriched.Versions.Single(version => version.ManifestId == "999");
        Assert.Equal(2048L, unmatched.SizeBytes);
        Assert.Null(unmatched.BuildLabel);

        await fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        Assert.Equal("original older manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.Equal("101;aabbccddeeff0011", File.ReadAllText(Path.Combine(session, "100.key")).Trim());

        await fixture.Service.ResumeDownloadAsync(100, fixture.Target);
        Assert.Equal(1, metadata.Calls);
        Assert.Equal(1, fixture.Source.Calls);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
    }

    [Fact]
    public async Task AChangingSourceCannotOverwriteAnotherPreparedVersion()
    {
        using var fixture = new Fixture();
        fixture.Source.VersionPerCall = true;
        fixture.Source.Delay = TimeSpan.FromMilliseconds(40);
        var preparations = await Task.WhenAll(
            fixture.Service.PrepareDownloadAsync(100, ManifestSource.Zaza),
            fixture.Service.PrepareDownloadAsync(100, ManifestSource.Zaza));
        Assert.Equal(1, fixture.Source.MaximumActive);
        Assert.NotEqual(preparations[0].Plan!.Id, preparations[1].Plan!.Id);
        Assert.Equal("111", Assert.Single(preparations[0].Plan!.Depots).DefaultManifestId);
        Assert.Equal("222", Assert.Single(preparations[1].Plan!.Depots).DefaultManifestId);

        await fixture.Service.DownloadPreparedAsync(preparations[0].Plan!, [new(101, "111")], fixture.Target);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal("manifest for call 1", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.Equal(2, fixture.Source.Calls);
    }

    [Fact]
    public async Task ClosingAPlanDuringStartKeepsItsFilesUntilItsUserFinishes()
    {
        using var fixture = new Fixture();
        fixture.Http.Block = true;
        var preparation = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.DepotBox);
        var plan = preparation.Plan!;
        var download = fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);
        await fixture.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Service.DiscardPreparedDownload(plan.Id);

        Assert.Single(System.IO.Directory.GetDirectories(Path.Combine(fixture.Work, "prepared")));
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        fixture.Http.Release.TrySetResult();
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(System.IO.Directory.GetDirectories(Path.Combine(fixture.Work, "prepared")));
        var closed = await fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target);
        Assert.False(closed.Succeeded);
        Assert.Contains("expired", closed.Message);
    }

    [Fact]
    public async Task CancellationAfterPinningLeavesARecoverableSelectedSubset()
    {
        using var fixture = new Fixture();
        fixture.Http.Block = true;
        var plan = (await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Hubcap)).Plan!;
        using var cancellation = new CancellationTokenSource();
        var download = fixture.Service.DownloadPreparedAsync(plan, [new(101, "111")], fixture.Target, cancellationToken: cancellation.Token);
        await fixture.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        Assert.Equal("original older manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.Empty(System.IO.Directory.GetFiles(session, "*.tmp"));
    }

    [Fact]
    public async Task AnInvalidSelectionCannotWriteAResumeSessionOrFetchATool()
    {
        using var fixture = new Fixture();
        var plan = (await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi)).Plan!;
        var forged = plan with { Depots = [new(999, "Injected depot", [new("777")], "777")] };
        var result = await fixture.Service.DownloadPreparedAsync(forged, [new(999, "777")], fixture.Target);
        Assert.False(result.Succeeded);
        Assert.Equal(0, fixture.Http.Calls);
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Work, "resume")));
    }

    [Fact]
    public async Task ACancelledPreparationReleasesItsSourceSlotAndKeepsNoSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Source.Delay = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource();
        var preparation = fixture.Service.PrepareDownloadAsync(100, ManifestSource.Ryuu, cancellationToken: cancellation.Token);
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Work, "prepared")));
        fixture.Source.Delay = TimeSpan.Zero;
        Assert.True((await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Ryuu)).Succeeded);
    }

    [Fact]
    public async Task OversizedSourceKeysFailBeforeKeepingASnapshotOrFetchingATool()
    {
        using var fixture = new Fixture();
        fixture.Source.OversizedKeyMetadata = true;

        var result = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Contains("source depot keys", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Http.Calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Work, "prepared")));
        fixture.Source.OversizedKeyMetadata = false;
        Assert.True((await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi)).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptResumeSelectionCannotSwitchToANewerSourceCache(bool corrupt)
    {
        using var fixture = new Fixture();
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "101_111.manifest"), "saved older manifest");
        if (corrupt) File.WriteAllText(Path.Combine(session, "resume.json"), "{ truncated selection");
        var newer = Path.Combine(fixture.Work, "100");
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(newer, "100.lua"), "setManifestid(101,\"999\")");
        File.WriteAllText(Path.Combine(newer, "101_999.manifest"), "newer source version");
        Directory.CreateDirectory(fixture.Target);
        var existing = Path.Combine(fixture.Target, "existing-game.bin");
        File.WriteAllText(existing, "Existing game files survive a failed resume.");

        var result = await fixture.Service.ResumeDownloadAsync(100, fixture.Target);

        Assert.False(result.Succeeded);
        Assert.Contains("saved depot selection", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start a new download", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Source.Calls);
        Assert.Equal(0, fixture.Http.Calls);
        Assert.Equal("saved older manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.False(File.Exists(Path.Combine(session, "101_999.manifest")));
        Assert.Equal("Existing game files survive a failed resume.", File.ReadAllText(existing));
    }

    [Fact]
    public async Task ResumeUsesTheSavedSubsetWhenANewerSourceCacheExists()
    {
        using var fixture = new Fixture();
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Directory.CreateDirectory(session);
        await DepotResumeStateStore.WriteAsync(session, new(100, Path.GetFullPath(fixture.Target), [new(101, "111")]), CancellationToken.None);
        File.WriteAllText(Path.Combine(session, "101_111.manifest"), "saved older manifest");
        var newer = Path.Combine(fixture.Work, "100");
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(newer, "100.lua"), "setManifestid(101,\"999\")\nsetManifestid(102,\"222\")");
        File.WriteAllText(Path.Combine(newer, "101_999.manifest"), "newer source version");
        File.WriteAllText(Path.Combine(newer, "102_222.manifest"), "unselected depot");

        var result = await fixture.Service.ResumeDownloadAsync(100, fixture.Target);

        Assert.False(result.Succeeded); // A tool fetch uses only the fake HTTP handler and cannot launch a process.
        Assert.Equal(0, fixture.Source.Calls);
        Assert.True(fixture.Http.Calls > 0);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        Assert.Equal("saved older manifest", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.False(File.Exists(Path.Combine(session, "101_999.manifest")));
        Assert.False(File.Exists(Path.Combine(session, "102_222.manifest")));
    }

    [Fact]
    public async Task CancellingASecondSelectionCannotReplaceTheActiveDownloadsPinnedFiles()
    {
        using var fixture = new Fixture();
        fixture.Source.VersionPerCall = true;
        fixture.Http.Block = true;
        var firstPlan = (await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Zaza)).Plan!;
        var secondPlan = (await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Zaza)).Plan!;
        var first = fixture.Service.DownloadPreparedAsync(firstPlan, [new(101, "111")], fixture.Target);
        await fixture.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var progress = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource();

        var second = fixture.Service.DownloadPreparedAsync(secondPlan, [new(101, "222")], fixture.Target,
            new InlineProgress(progress.Enqueue), cancellation.Token);

        Assert.Empty(progress); // The target's active session is locked before writing any replacement selection.
        Assert.Equal(1, fixture.Http.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var session = DepotResumeStateStore.SessionDirectory(fixture.Work, 100, fixture.Target);
        Assert.Equal(new CachedDepotManifest(101, "111"), Assert.Single(DepotResumeStateStore.Read(session, 100, fixture.Target)!.Depots));
        Assert.Equal("manifest for call 1", File.ReadAllText(Path.Combine(session, "101_111.manifest")));
        Assert.False(File.Exists(Path.Combine(session, "101_222.manifest")));
        Assert.Equal(1, fixture.Http.Calls);
        fixture.Http.Release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task PreparedSelectionsAreBoundedAndDiscardOrCancellationReleasesCapacity()
    {
        using var fixture = new Fixture();
        var plans = new List<PreparedGameDownload>();
        for (var index = 0; index < RyuuGameDownloadService.MaximumPreparedDownloads; index++)
        {
            var preparation = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi);
            plans.Add(Assert.IsType<PreparedGameDownload>(preparation.Plan));
        }
        var callsBefore = fixture.Source.Calls;

        var full = await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi);

        Assert.False(full.Succeeded);
        Assert.Null(full.Plan);
        Assert.Contains("Too many source selections", full.Message);
        Assert.Equal(callsBefore, fixture.Source.Calls);
        Assert.Equal(0, fixture.Http.Calls);
        fixture.Service.DiscardPreparedDownload(plans[0].Id);
        Assert.True((await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi)).Succeeded);
        fixture.Service.DiscardPreparedDownload(plans[1].Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi,
            cancellationToken: cancellation.Token));
        Assert.True((await fixture.Service.PrepareDownloadAsync(100, ManifestSource.Sushi)).Succeeded);
        Assert.Equal(callsBefore + 2, fixture.Source.Calls);
    }

    public class NoCallsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.DeclaringType == typeof(ILoggingService) && targetMethod.Name == "Add") return null;
            throw new InvalidOperationException("Unexpected external dependency call: " + targetMethod?.Name);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "steamy-prepared-tests-" + Guid.NewGuid().ToString("N"));
        public string Work => Path.Combine(_root, "work");
        public string Target => Path.Combine(_root, "game");
        public SourceFixture Source { get; }
        public DeferredHttpHandler Http { get; } = new();
        public HttpClient Client { get; }
        public RyuuGameDownloadService Service { get; }
        public Fixture(ISteamDepotMetadataService? depotMetadata = null)
        {
            Source = new(Path.Combine(_root, "source"));
            Client = new(Http);
            Service = new(DispatchProxy.Create<ISettingsService, NoCallsProxy>(),
                DispatchProxy.Create<ISecureCredentialService, NoCallsProxy>(),
                DispatchProxy.Create<IRyuuSecureDownloadService, NoCallsProxy>(), Source,
                DispatchProxy.Create<ILoggingService, NoCallsProxy>(), Client, Work, Path.Combine(_root, "missing-tools"), depotMetadata);
        }
        public void Dispose()
        {
            Http.Release.TrySetResult();
            Service.Dispose();
            Client.Dispose();
            if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true);
        }
    }

    private sealed class MetadataFixture : ISteamDepotMetadataService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<PreparedDownloadDepot>> EnrichAsync(int appId,
            IReadOnlyList<PreparedDownloadDepot> sourceDepots, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(100, appId);
            return Task.FromResult(SteamDepotMetadataReader.Enrich(sourceDepots,
                new Dictionary<int, SteamDepotMetadata>
                {
                    [101] = new("Windows game content", "Game content", "Windows", "English", null, null,
                        new Dictionary<string, SteamDepotManifestMetadata>
                        {
                            ["111"] = new("111", 512, 400, "Build 42", "public")
                        })
                }));
        }
    }

    private sealed class SourceFixture(string directory) : IManifestSourceService
    {
        public string Directory { get; } = directory;
        public int Calls { get; private set; }
        public int MaximumActive { get; private set; }
        public bool VersionPerCall { get; set; }
        public bool OversizedKeyMetadata { get; set; }
        public TimeSpan Delay { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public IReadOnlyList<ManifestSourceInfo> Sources => [];
        public Task<ManifestAvailability> CheckAvailabilityAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Preparation must fetch the selected source once.");
        public async Task<ManifestDownloadResult> DownloadManifestsAsync(ManifestSource source, int appId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            _active++;
            MaximumActive = Math.Max(MaximumActive, _active);
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Delay, cancellationToken);
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var old in System.IO.Directory.GetFiles(Directory)) File.Delete(old);
                string lua;
                if (VersionPerCall)
                {
                    var manifest = (Calls * 111).ToString();
                    lua = $"setManifestid(101, \"{manifest}\")";
                    File.WriteAllText(Path.Combine(Directory, $"101_{manifest}.manifest"), $"manifest for call {Calls}");
                }
                else
                {
                    lua = "addappid(101,1,\"aabbccddeeff0011\")\nsetManifestid(101,\"999\",2048)\nsetManifestid(102,\"222\")";
                    File.WriteAllText(Path.Combine(Directory, "101_111.manifest"), "original older manifest");
                    File.WriteAllText(Path.Combine(Directory, "101_999.manifest"), "current manifest");
                    File.WriteAllText(Path.Combine(Directory, "102_222.manifest"), "other depot");
                }
                File.WriteAllText(Path.Combine(Directory, $"{appId}.lua"), lua);
                if (OversizedKeyMetadata)
                    File.WriteAllBytes(Path.Combine(Directory, $"{appId}.key"), new byte[4 * 1024 * 1024 + 1]);
                return new(true, "Offline source package", lua, Directory);
            }
            finally { _active--; }
        }
    }

    private sealed class DeferredHttpHandler : HttpMessageHandler
    {
        public bool Block { get; set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.NotFound) { Content = new StringContent("No network or executable in this fixture") };
        }
    }
}
