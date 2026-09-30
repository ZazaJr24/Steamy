using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Steamy.Services;

public sealed record FreeManifestIndex(bool Succeeded, IReadOnlySet<int> AppIds, string Message);

public interface IFreeManifestCatalogService
{
    Task<FreeManifestIndex> GetAsync(string source, bool force = false, CancellationToken cancellationToken = default);
}

public sealed class FreeManifestCatalogService : IFreeManifestCatalogService, IDisposable
{
    public const string SushiRepository = "sushi-dev55/sushitools-games-repo";
    public const string SushiUrl = "https://github.com/" + SushiRepository;
    public const string SushiCommunityUrl = "https://discord.gg/sushitools";
    private readonly HttpClient _client = new(StableDnsHandler.Create()) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, (DateTimeOffset At, FreeManifestIndex Index)> _memory = new();
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "source-indexes");

    public FreeManifestCatalogService() => _client.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

    public async Task<FreeManifestIndex> GetAsync(string source, bool force = false, CancellationToken cancellationToken = default)
    {
        if (source is not ("Sushi" or "Zaza")) throw new ArgumentOutOfRangeException(nameof(source));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _memory.TryGetValue(source, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromHours(1)) return cached.Index;
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, source + ".json");
            if (!force && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1))
            {
                var disk = await ReadCacheAsync(path, cancellationToken).ConfigureAwait(false);
                if (disk is not null) return Remember(source, disk);
            }
            try
            {
                var repo = source == "Sushi" ? SushiRepository : "ZazaJr24/Game-Files-UpdateR";
                var json = await _client.GetStringAsync($"https://api.github.com/repos/{repo}/git/trees/main", cancellationToken).ConfigureAwait(false);
                if (source == "Zaza")
                {
                    using var tree = JsonDocument.Parse(json);
                    var manifests = tree.RootElement.GetProperty("tree").EnumerateArray().First(entry => entry.GetProperty("path").GetString() == "Manifests");
                    json = await _client.GetStringAsync($"https://api.github.com/repos/{repo}/git/trees/{manifests.GetProperty("sha").GetString()}", cancellationToken).ConfigureAwait(false);
                }
                var ids = GitHubManifestIndex.Read(json, source == "Sushi");
                var temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ids), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                return Remember(source, new(true, ids, $"{ids.Count:N0} apps available on {source} · free, no API key"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException or InvalidOperationException)
            {
                var disk = await ReadCacheAsync(path, cancellationToken).ConfigureAwait(false);
                return disk is not null ? Remember(source, disk with { Message = $"{source} unavailable; showing the saved source index." })
                    : new(false, new HashSet<int>(), $"{source} source index unavailable; you can still select it for a download.");
            }
        }
        finally { _gate.Release(); }
    }

    private FreeManifestIndex Remember(string source, FreeManifestIndex index)
    {
        _memory[source] = (DateTimeOffset.UtcNow, index);
        return index;
    }

    private static async Task<FreeManifestIndex?> ReadCacheAsync(string path, CancellationToken token)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<HashSet<int>>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            return ids is null ? null : new(true, ids.Where(id => id > 0).ToHashSet(), "Loaded saved source index.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _client.Dispose();
}
