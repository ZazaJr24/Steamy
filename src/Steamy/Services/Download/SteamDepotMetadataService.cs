using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Steamy.Services;

public interface ISteamDepotMetadataService
{
    Task<IReadOnlyList<PreparedDownloadDepot>> EnrichAsync(int appId,
        IReadOnlyList<PreparedDownloadDepot> sourceDepots, CancellationToken cancellationToken = default);
}

public sealed record SteamDepotManifestMetadata(string ManifestId, long? SizeBytes,
    long? CompressedSizeBytes, string? BuildLabel, string BranchName);

public sealed record SteamDepotMetadata(string? Name, string? ContentType, string? OperatingSystems,
    string? Languages, int? DlcAppId, int? SharedAppId,
    IReadOnlyDictionary<string, SteamDepotManifestMetadata> Manifests);

/// <summary>Parses public Steam app-info mirrored by SteamCMD; it does not supply downloadable depots.</summary>
public static class SteamDepotMetadataReader
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const string Provenance = "Steam app info · SteamCMD mirror";
    public const int MaximumDepotEntries = 4096;
    private const int MaximumBranches = 128;

    public static IReadOnlyDictionary<int, SteamDepotMetadata> Read(int appId, ReadOnlyMemory<byte> utf8Json)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        if (utf8Json.Length > MaximumResponseBytes) throw new InvalidDataException("Steam app-info exceeds the metadata limit.");
        using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 48 });
        var result = new Dictionary<int, SteamDepotMetadata>();
        var root = document.RootElement;
        if (!TryObject(root, "data", out var data)
            || !TryObject(data, appId.ToString(CultureInfo.InvariantCulture), out var app)
            || !TryObject(app, "depots", out var depots)) return ReadOnly(result);

        var builds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryObject(depots, "branches", out var branches))
        {
            var branchCount = 0;
            foreach (var branch in branches.EnumerateObject())
            {
                if (++branchCount > MaximumBranches) throw new InvalidDataException("Steam app-info contains too many branches.");
                if (branch.Value.ValueKind != JsonValueKind.Object) continue;
                var buildId = NumberString(branch.Value, "buildid");
                if (buildId is not null && branch.Name.Length <= 100) builds.TryAdd(branch.Name, $"Build {buildId}");
            }
        }

        var depotCount = 0;
        foreach (var depot in depots.EnumerateObject())
        {
            if (++depotCount > MaximumDepotEntries) throw new InvalidDataException("Steam app-info contains too many depot entries.");
            if (!int.TryParse(depot.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var depotId)
                || depotId <= 0 || depot.Value.ValueKind != JsonValueKind.Object) continue;
            var value = depot.Value;
            var name = Text(value, "name", 160);
            var dlcAppId = PositiveInt(value, "dlcappid");
            var sharedAppId = PositiveInt(value, "depotfromapp");
            string? operatingSystems = null, languages = null;
            if (TryObject(value, "config", out var config))
            {
                operatingSystems = ListText(Text(config, "oslist", 100));
                languages = ListText(Text(config, "language", 100) ?? Text(config, "languages", 100));
            }
            var versions = new Dictionary<string, SteamDepotManifestMetadata>(StringComparer.Ordinal);
            if (TryObject(value, "manifests", out var manifests))
            {
                // Prefer the public branch if multiple branches refer to the same manifest.
                var entries = manifests.EnumerateObject().ToArray();
                if (entries.Length > MaximumBranches) throw new InvalidDataException("Steam app-info contains too many depot manifests.");
                foreach (var manifest in entries.OrderBy(entry => entry.Name == "public" ? 0 : 1).ThenBy(entry => entry.Name, StringComparer.Ordinal))
                {
                    if (manifest.Name.Length > 100 || manifest.Value.ValueKind != JsonValueKind.Object) continue;
                    var gid = NumberString(manifest.Value, "gid");
                    if (!DownloadPreparationReader.IsManifestId(gid)) continue;
                    versions.TryAdd(gid!, new(gid!, PositiveLong(manifest.Value, "size"),
                        PositiveLong(manifest.Value, "download"), builds.GetValueOrDefault(manifest.Name), manifest.Name));
                }
            }
            var contentType = dlcAppId.HasValue ? "DLC" : sharedAppId.HasValue ? "Shared content"
                : languages is not null ? "Language content" : null;
            result.TryAdd(depotId, new(name, contentType, operatingSystems, languages, dlcAppId, sharedAppId,
                new ReadOnlyDictionary<string, SteamDepotManifestMetadata>(versions)));
        }
        return ReadOnly(result);
    }

    public static IReadOnlyList<PreparedDownloadDepot> Enrich(IReadOnlyList<PreparedDownloadDepot> sourceDepots,
        IReadOnlyDictionary<int, SteamDepotMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(sourceDepots);
        ArgumentNullException.ThrowIfNull(metadata);
        return Array.AsReadOnly(sourceDepots.Select(depot =>
        {
            var steamDbUrl = $"https://steamdb.info/depot/{depot.DepotId.ToString(CultureInfo.InvariantCulture)}/";
            if (!metadata.TryGetValue(depot.DepotId, out var info)) return depot with { SteamDbUrl = steamDbUrl };
            var versions = depot.Versions.Select(version =>
            {
                // A current branch's size or build must never be attributed to an older source manifest.
                if (!info.Manifests.TryGetValue(version.ManifestId, out var exact)) return version;
                return version with
                {
                    SizeBytes = version.SizeBytes ?? exact.SizeBytes,
                    BuildLabel = version.BuildLabel ?? exact.BuildLabel,
                    CompressedSizeBytes = version.CompressedSizeBytes ?? exact.CompressedSizeBytes,
                    BranchName = version.BranchName ?? exact.BranchName
                };
            }).ToArray();
            return depot with
            {
                Name = info.Name ?? depot.Name,
                Versions = Array.AsReadOnly(versions),
                ContentType = info.ContentType,
                OperatingSystems = info.OperatingSystems,
                Languages = info.Languages,
                MetadataSource = Provenance,
                SteamDbUrl = steamDbUrl,
                DlcAppId = info.DlcAppId,
                SharedAppId = info.SharedAppId
            };
        }).ToArray());
    }

    private static IReadOnlyDictionary<int, SteamDepotMetadata> ReadOnly(Dictionary<int, SteamDepotMetadata> metadata) =>
        new ReadOnlyDictionary<int, SteamDepotMetadata>(metadata);

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static string? Text(JsonElement parent, string name, int maximumLength)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl)) return null;
        return text;
    }

    private static string? NumberString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        return text is { Length: > 0 and <= 20 } && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? text : null;
    }

    private static long? PositiveLong(JsonElement parent, string name) => NumberString(parent, name) is { } value
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;

    private static int? PositiveInt(JsonElement parent, string name) => NumberString(parent, name) is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static string? ListText(string? value)
    {
        if (value is null) return null;
        var list = string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return list.Length == 0 ? null : list;
    }
}

/// <summary>Optional best-effort metadata, fetched once during preparation; source versions remain authoritative.</summary>
public sealed class SteamDepotMetadataService : ISteamDepotMetadataService, IDisposable
{
    public const int MaximumCacheEntries = 64;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly IReadOnlyDictionary<int, SteamDepotMetadata> Empty =
        new ReadOnlyDictionary<int, SteamDepotMetadata>(new Dictionary<int, SteamDepotMetadata>());
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<int, CachedMetadata> _cache = [];
    private readonly Dictionary<int, Task<IReadOnlyDictionary<int, SteamDepotMetadata>>> _pending = [];
    private readonly SemaphoreSlim _requestSlots = new(4, 4);
    private readonly CancellationTokenSource _shutdown = new();
    private long _lastAccess;
    private sealed record CachedMetadata(IReadOnlyDictionary<int, SteamDepotMetadata> Value,
        DateTimeOffset ExpiresAt, long LastAccess);

    public SteamDepotMetadataService(HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PreparedDownloadDepot>> EnrichAsync(int appId,
        IReadOnlyList<PreparedDownloadDepot> sourceDepots, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        ArgumentNullException.ThrowIfNull(sourceDepots);
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceDepots.Count == 0) return sourceDepots;
        Task<IReadOnlyDictionary<int, SteamDepotMetadata>> request;
        lock (_sync)
        {
            if (_cache.TryGetValue(appId, out var cached) && cached.ExpiresAt > _time.GetUtcNow())
            {
                _cache[appId] = cached with { LastAccess = ++_lastAccess };
                return SteamDepotMetadataReader.Enrich(sourceDepots, cached.Value);
            }
            if (!_pending.TryGetValue(appId, out request!))
            {
                if (_pending.Count >= MaximumCacheEntries)
                    return SteamDepotMetadataReader.Enrich(sourceDepots, Empty);
                // Task.Run prevents a synchronous fake/fast HTTP response from publishing before registration.
                request = Task.Run(() => FetchAndCacheAsync(appId), CancellationToken.None);
                _pending.Add(appId, request);
            }
        }
        // A closed selection cancels only its own wait, not another selection's shared metadata request.
        var metadata = await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SteamDepotMetadataReader.Enrich(sourceDepots, metadata);
    }

    private async Task<IReadOnlyDictionary<int, SteamDepotMetadata>> FetchAndCacheAsync(int appId)
    {
        IReadOnlyDictionary<int, SteamDepotMetadata> metadata = Empty;
        var successful = false;
        var acquired = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            await _requestSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.steamcmd.net/v1/info/{appId.ToString(CultureInfo.InvariantCulture)}");
            request.Headers.UserAgent.ParseAdd("Steamy/0.4.1 (+https://github.com/ZazaJr24/Steamy)");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > SteamDepotMetadataReader.MaximumResponseBytes)
                throw new InvalidDataException("Steam app-info exceeds the metadata limit.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var payload = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var bytes = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (bytes == 0) break;
                if (payload.Length + bytes > SteamDepotMetadataReader.MaximumResponseBytes)
                    throw new InvalidDataException("Steam app-info exceeds the metadata limit.");
                await payload.WriteAsync(buffer.AsMemory(0, bytes), timeout.Token).ConfigureAwait(false);
            }
            metadata = SteamDepotMetadataReader.Read(appId, payload.ToArray());
            successful = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException
            or OperationCanceledException or InvalidOperationException)
        {
            // A mirror outage never prevents source selection or invents metadata for old manifests.
        }
        finally
        {
            if (acquired) _requestSlots.Release();
            lock (_sync)
            {
                _pending.Remove(appId);
                _cache[appId] = new(metadata,
                    _time.GetUtcNow() + (successful ? SuccessCacheLifetime : FailureCacheLifetime), ++_lastAccess);
                while (_cache.Count > MaximumCacheEntries)
                    _cache.Remove(_cache.MinBy(entry => entry.Value.LastAccess).Key);
            }
        }
        return metadata;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
        // In-flight requests release their slots before completion; do not dispose their semaphore.
    }
}
