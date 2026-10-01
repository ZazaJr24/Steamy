using System.Net;
using System.Text;
using System.Text.Json;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class SteamDepotMetadataTests
{
    private const string AppInfo = """
        {
          "data": {
            "100": {
              "depots": {
                "101": {
                  "name": "Game Content",
                  "config": {"oslist": "windows,linux"},
                  "manifests": {
                    "beta": {"gid": "111", "size": "9000", "download": "4500"},
                    "public": {"gid": "111", "size": "8000", "download": "4000"}
                  }
                },
                "102": {
                  "name": "English Audio",
                  "config": {"oslist": "windows", "language": "english"},
                  "manifests": {"public": {"gid": "222", "size": 3000, "download": 2000}}
                },
                "103": {"name": "Expansion", "dlcappid": "900", "manifests": {}},
                "104": {"name": "Shared Runtime", "depotfromapp": "200", "manifests": {}},
                "branches": {"beta": {"buildid": "500"}, "public": {"buildid": "400"}}
              }
            }
          },
          "status": "success"
        }
        """;

    [Fact]
    public void ParserReadsSteamNamesPlatformsLanguagesAndDepotRelationships()
    {
        var metadata = SteamDepotMetadataReader.Read(100, Encoding.UTF8.GetBytes(AppInfo));

        Assert.Equal(4, metadata.Count);
        Assert.Equal("Game Content", metadata[101].Name);
        Assert.Equal("windows, linux", metadata[101].OperatingSystems);
        Assert.Equal("english", metadata[102].Languages);
        Assert.Equal("Language content", metadata[102].ContentType);
        Assert.Equal("DLC", metadata[103].ContentType);
        Assert.Equal(900, metadata[103].DlcAppId);
        Assert.Equal("Shared content", metadata[104].ContentType);
        Assert.Equal(200, metadata[104].SharedAppId);
        var manifest = Assert.Single(metadata[101].Manifests).Value;
        Assert.Equal("public", manifest.BranchName);
        Assert.Equal("Build 400", manifest.BuildLabel);
        Assert.Equal(8000, manifest.SizeBytes);
        Assert.Equal(4000, manifest.CompressedSizeBytes);
    }

    [Fact]
    public void EnrichmentKeepsSourceDepotsAndVersionsAndOnlyAddsExactManifestSizes()
    {
        PreparedDownloadDepot[] source =
        [
            new(101, "Depot 101", [new("99"), new("111")], "99"),
            new(777, "Source-only depot", [new("888")], "888")
        ];
        var metadata = SteamDepotMetadataReader.Read(100, Encoding.UTF8.GetBytes(AppInfo));

        var enriched = SteamDepotMetadataReader.Enrich(source, metadata);

        Assert.Equal([101, 777], enriched.Select(depot => depot.DepotId));
        Assert.Equal("Game Content", enriched[0].Name);
        Assert.Equal("99", enriched[0].DefaultManifestId);
        Assert.Equal(["99", "111"], enriched[0].Versions.Select(version => version.ManifestId));
        Assert.Null(enriched[0].Versions[0].SizeBytes);
        Assert.Null(enriched[0].Versions[0].CompressedSizeBytes);
        Assert.Null(enriched[0].Versions[0].BuildLabel);
        Assert.Null(enriched[0].Versions[0].BranchName);
        Assert.Equal(8000, enriched[0].Versions[1].SizeBytes);
        Assert.Equal("Build 400", enriched[0].Versions[1].BuildLabel);
        Assert.Equal(SteamDepotMetadataReader.Provenance, enriched[0].MetadataSource);
        Assert.Equal("https://steamdb.info/depot/101/", enriched[0].SteamDbUrl);
        Assert.Equal("Source-only depot", enriched[1].Name);
        Assert.Null(enriched[1].MetadataSource);
        Assert.Equal("https://steamdb.info/depot/777/", enriched[1].SteamDbUrl);
        Assert.Null(source[0].Versions[1].SizeBytes); // The pinned source snapshot was not mutated.
    }

    [Fact]
    public void ExplicitSourceBuildAndSizeHavePrecedenceOverSteamDisplayMetadata()
    {
        PreparedDownloadDepot[] source = [new(101, "Depot 101", [new("111", 7000, "Version 1.2")], "111")];

        var enriched = SteamDepotMetadataReader.Enrich(source, SteamDepotMetadataReader.Read(100, Encoding.UTF8.GetBytes(AppInfo)));

        var version = Assert.Single(Assert.Single(enriched).Versions);
        Assert.Equal(7000, version.SizeBytes);
        Assert.Equal("Version 1.2", version.BuildLabel);
        Assert.Equal(4000, version.CompressedSizeBytes);
    }

    [Fact]
    public void WrongAppInfoCannotEnrichAnotherAppsDepots()
    {
        Assert.Empty(SteamDepotMetadataReader.Read(999, Encoding.UTF8.GetBytes(AppInfo)));
    }

    [Theory]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"100\":{\"depots\":[]}}}")]
    [InlineData("{\"data\":{\"100\":{\"depots\":{\"abc\":{},\"-1\":{},\"101\":null}}}}")]
    public void UnsupportedOrMissingMetadataRemainsUnknown(string json)
    {
        Assert.Empty(SteamDepotMetadataReader.Read(100, Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void InvalidSizesAndManifestIdentifiersAreNeverDisplayedAsAuthoritative()
    {
        const string invalid = """
          {"data":{"100":{"depots":{"101":{"name":"bad\nname","config":{"language":", ,"},
            "manifests":{"bad":{"gid":"-123","size":"42"},"public":{"gid":"111","size":"-9","download":"9223372036854775808"}}},
            "branches":{"public":{"buildid":"not a build"}}}}}}
          """;
        var depot = Assert.Single(SteamDepotMetadataReader.Read(100, Encoding.UTF8.GetBytes(invalid))).Value;

        Assert.Null(depot.Name);
        Assert.Null(depot.Languages);
        Assert.Null(depot.ContentType);
        var version = Assert.Single(depot.Manifests).Value;
        Assert.Equal("111", version.ManifestId);
        Assert.Null(version.SizeBytes);
        Assert.Null(version.CompressedSizeBytes);
        Assert.Null(version.BuildLabel);
    }

    [Fact]
    public void ReaderRejectsOversizedPayloadAndTooManyDepotEntries()
    {
        Assert.Throws<InvalidDataException>(() => SteamDepotMetadataReader.Read(100,
            new byte[SteamDepotMetadataReader.MaximumResponseBytes + 1]));
        var depots = Enumerable.Range(1, SteamDepotMetadataReader.MaximumDepotEntries + 1)
            .ToDictionary(id => id.ToString(), _ => new { name = "Content" });
        var json = JsonSerializer.SerializeToUtf8Bytes(new { data = new Dictionary<string, object> { ["100"] = new { depots } } });
        Assert.Throws<InvalidDataException>(() => SteamDepotMetadataReader.Read(100, json));
    }

    [Fact]
    public async Task ConcurrentSelectionsShareOneFetchAndUseBoundedCachedMetadata()
    {
        using var handler = new MetadataHandler(AppInfo) { Block = true };
        using var http = new HttpClient(handler);
        using var service = new SteamDepotMetadataService(http);
        PreparedDownloadDepot[] source = [new(101, "Depot 101", [new("111")], "111")];

        var first = service.EnrichAsync(100, source);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.EnrichAsync(100, source);
        handler.Release.TrySetResult();
        var results = await Task.WhenAll(first, second);
        var cached = await service.EnrichAsync(100, source);

        Assert.Equal(1, handler.Calls);
        Assert.All(results, result => Assert.Equal("Game Content", Assert.Single(result).Name));
        Assert.Equal(8000, Assert.Single(Assert.Single(cached).Versions).SizeBytes);
        Assert.Equal("https://api.steamcmd.net/v1/info/100", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ClosingOneSelectionDoesNotCancelAnotherSelectionsSharedRequest()
    {
        using var handler = new MetadataHandler(AppInfo) { Block = true };
        using var http = new HttpClient(handler);
        using var service = new SteamDepotMetadataService(http);
        PreparedDownloadDepot[] source = [new(101, "Depot 101", [new("111")], "111")];
        using var cancellation = new CancellationTokenSource();
        var first = service.EnrichAsync(100, source, cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.EnrichAsync(100, source);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        handler.Release.TrySetResult();
        var result = await second;

        Assert.Equal("Game Content", Assert.Single(result).Name);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OutagesAreCachedBrieflyAndNeverBlockSourceProvidedVersions()
    {
        using var handler = new MetadataHandler(AppInfo) { StatusCode = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var time = new MutableTimeProvider();
        using var service = new SteamDepotMetadataService(http, time);
        PreparedDownloadDepot[] source = [new(101, "Source depot", [new("99")], "99")];

        var result = await service.EnrichAsync(100, source);
        await service.EnrichAsync(100, source);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Source depot", Assert.Single(result).Name);
        Assert.Equal("99", Assert.Single(Assert.Single(result).Versions).ManifestId);
        Assert.Null(Assert.Single(result).MetadataSource);

        time.Advance(TimeSpan.FromMinutes(2));
        handler.StatusCode = HttpStatusCode.OK;
        Assert.Equal("Game Content", Assert.Single(await service.EnrichAsync(100, source)).Name);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulCacheExpiresAndLeastRecentlyUsedAppIsEvicted()
    {
        using var handler = new MetadataHandler(AppInfo);
        using var http = new HttpClient(handler);
        var time = new MutableTimeProvider();
        using var service = new SteamDepotMetadataService(http, time);
        PreparedDownloadDepot[] source = [new(101, "Depot 101", [new("111")], "111")];
        await service.EnrichAsync(100, source);
        await service.EnrichAsync(100, source);
        Assert.Equal(1, handler.Calls);
        time.Advance(TimeSpan.FromMinutes(31));
        await service.EnrichAsync(100, source);
        Assert.Equal(2, handler.Calls);
        for (var appId = 200; appId < 200 + SteamDepotMetadataService.MaximumCacheEntries; appId++)
            await service.EnrichAsync(appId, source);
        await service.EnrichAsync(100, source);
        Assert.Equal(SteamDepotMetadataService.MaximumCacheEntries + 3, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedResponsesAreRejectedWithAndWithoutContentLength(bool contentLength)
    {
        using var handler = new MetadataHandler(AppInfo)
        {
            ResponseBytes = new byte[SteamDepotMetadataReader.MaximumResponseBytes + 1],
            ReportContentLength = contentLength
        };
        using var http = new HttpClient(handler);
        using var service = new SteamDepotMetadataService(http);
        PreparedDownloadDepot[] source = [new(101, "Depot 101", [new("111")], "111")];

        var result = await service.EnrichAsync(100, source);

        Assert.Equal("Depot 101", Assert.Single(result).Name);
        Assert.Null(Assert.Single(result).MetadataSource);
        Assert.Null(Assert.Single(Assert.Single(result).Versions).SizeBytes);
    }

    [Fact]
    public async Task CancellingBeforePreparationMakesNoMetadataRequest()
    {
        using var handler = new MetadataHandler(AppInfo);
        using var http = new HttpClient(handler);
        using var service = new SteamDepotMetadataService(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnrichAsync(100,
            [new(101, "Depot 101", [new("111")], "111")], cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AStalledMetadataMirrorTimesOutWithoutChangingSourceSelection()
    {
        using var handler = new MetadataHandler(AppInfo) { Block = true };
        using var http = new HttpClient(handler);
        using var service = new SteamDepotMetadataService(http);
        PreparedDownloadDepot[] source = [new(101, "Source depot", [new("99")], "99")];

        var result = await service.EnrichAsync(100, source).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal("Source depot", Assert.Single(result).Name);
        Assert.Equal("99", Assert.Single(Assert.Single(result).Versions).ManifestId);
        Assert.Null(Assert.Single(result).MetadataSource);
        Assert.Equal(1, handler.Calls);
        Assert.False(handler.Release.Task.IsCompleted);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class MetadataHandler(string response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Block { get; init; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public byte[]? ResponseBytes { get; init; }
        public bool ReportContentLength { get; init; } = true;
        public Uri? RequestUri { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            RequestUri = request.RequestUri;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            var bytes = ResponseBytes ?? Encoding.UTF8.GetBytes(response);
            HttpContent content = ReportContentLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
            return new(StatusCode) { Content = content };
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
