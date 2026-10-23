using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Steamy.Models;

namespace Steamy.Services;

public sealed record GifItem(string Provider, string Id, string Title, string PreviewUrl, string ShareUrl);
public sealed record GifSearchPage(IReadOnlyList<GifItem> Items, bool HasMore);
public sealed class GifProviderException(string message) : Exception(message);
public interface IGifSearchService
{
    string Provider { get; }
    bool IsConfigured { get; }
    Task<GifSearchPage> SearchAsync(string query, int page, CancellationToken token);
    Task<GifSearchPage> FavoritesAsync(IReadOnlyList<string> ids, CancellationToken token);
}

/// <summary>Direct provider requests. Store provider IDs only, never search responses or media URLs.</summary>
public sealed partial class GifSearchService : IGifSearchService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private DateTimeOffset _retryAfter;
    private string _rateLimitedKey = "";
    public GifSearchService(ISettingsService settings) : this(settings.Load(), SharedClient) { }
    internal GifSearchService(AppSettings settings, HttpClient http) { _settings = settings; _http = http; }
    public string Provider => _settings.GifProvider == "GIPHY" ? "GIPHY" : "KLIPY";
    public bool IsConfigured => Key().Length > 0;
    [GeneratedRegex("^[A-Za-z0-9_-]{1,160}$")]
    private static partial Regex Identifier();
    internal static bool ValidId(string id) => Identifier().IsMatch(id);
    private string Key() => _settings.GifApiKey.Trim();
    public Task<GifSearchPage> SearchAsync(string query, int page, CancellationToken token)
    {
        if (query.Length > 50) throw new GifProviderException("Search can be up to 50 characters.");
        page = Math.Clamp(page, 1, 20);
        var trending = query.Length == 0;
        var parameters = Provider == "GIPHY" ? $"limit=24&offset={(page - 1) * 24}&rating=pg-13" : $"per_page=24&page={page}";
        if (!trending) parameters += "&q=" + Uri.EscapeDataString(query);
        return RequestAsync(trending ? "trending" : "search", parameters, token);
    }
    public Task<GifSearchPage> FavoritesAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        if (ids.Count == 0) return Task.FromResult(new GifSearchPage([], false));
        if (ids.Count > 50 || ids.Any(id => !ValidId(id))) throw new GifProviderException("Invalid GIF favorite.");
        return RequestAsync(Provider == "GIPHY" ? "" : "items", (Provider == "GIPHY" ? "ids=" : "slugs=") + Uri.EscapeDataString(string.Join(',', ids)), token);
    }
    private async Task<GifSearchPage> RequestAsync(string endpoint, string parameters, CancellationToken token)
    {
        var provider = Provider; var key = Key();
        if (key.Length == 0) throw new GifProviderException($"{provider} search needs an app key. You can paste a GIF link without one.");
        if (!ValidId(key)) throw new GifProviderException($"Check the {provider} app key.");
        if (_rateLimitedKey == key && DateTimeOffset.UtcNow < _retryAfter) throw new GifProviderException($"{provider}'s request limit was reached. Try again later.");
        var url = provider == "GIPHY" ? $"https://api.giphy.com/v1/gifs{(endpoint.Length > 0 ? "/" + endpoint : "")}?api_key={Uri.EscapeDataString(key)}&{parameters}"
            : $"https://api.klipy.com/api/v1/{Uri.EscapeDataString(key)}/gifs/{endpoint}?{parameters}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoStore = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _rateLimitedKey = key;
                _retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 1, 3600));
                throw new GifProviderException($"{provider}'s request limit was reached. Try again later.");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new GifProviderException($"{provider} couldn't accept this app key. Check the provider settings.");
            if (!response.IsSuccessStatusCode) throw new GifProviderException($"{provider} is unavailable. Try again shortly.");
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new GifProviderException("The GIF response was too large.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + read > limit) throw new GifProviderException("The GIF response was too large."); memory.Write(buffer, 0, read); }
            return Parse(memory.ToArray(), provider);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GifProviderException("GIF search timed out. Try again."); }
        catch (HttpRequestException) { throw new GifProviderException("GIF search couldn't connect. Check your connection."); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new GifProviderException("The GIF provider returned an unsupported response."); }
    }
    internal static GifSearchPage Parse(byte[] bytes, string provider)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var root = document.RootElement;
        if (provider != "GIPHY" && root.TryGetProperty("result", out var success) && success.ValueKind == JsonValueKind.False) throw new GifProviderException("KLIPY couldn't load GIFs. Check the app key and try again.");
        var container = root.GetProperty("data");
        var entries = provider == "GIPHY" ? container : container.GetProperty("data");
        var items = new List<GifItem>();
        foreach (var item in entries.EnumerateArray())
        {
            if (items.Count >= 50) throw new GifProviderException("The GIF provider returned too many results.");
            var id = item.GetProperty(provider == "GIPHY" ? "id" : "slug").GetString() ?? "";
            var title = item.TryGetProperty("title", out var label) ? label.GetString() ?? "GIF" : "GIF";
            if (title.Length > 200) title = title[..200];
            var images = item.GetProperty(provider == "GIPHY" ? "images" : "file");
            string preview, share;
            if (provider == "GIPHY")
            {
                preview = images.GetProperty("fixed_width_small").GetProperty("url").GetString()!;
                share = (images.TryGetProperty("downsized", out var small) ? small : images.GetProperty("original")).GetProperty("url").GetString()!;
            }
            else
            {
                var thumb = images.TryGetProperty("xs", out var xs) ? xs : images.GetProperty("sm");
                preview = thumb.GetProperty("gif").GetProperty("url").GetString()!;
                share = (images.TryGetProperty("md", out var md) ? md : images.GetProperty("hd")).GetProperty("gif").GetProperty("url").GetString()!;
            }
            if (!ValidId(id) || !ValidMedia(preview, provider) || !ValidMedia(share, provider)) throw new GifProviderException("The GIF provider returned an unsupported media link.");
            items.Add(new(provider, id, title, preview, share));
        }
        var hasMore = provider == "GIPHY" ? root.TryGetProperty("pagination", out var pages) && pages.GetProperty("offset").GetInt32() + pages.GetProperty("count").GetInt32() < pages.GetProperty("total_count").GetInt32()
            : container.TryGetProperty("has_next", out var next) && next.ValueKind == JsonValueKind.True;
        return new(items, hasMore);
    }
    private static bool ValidMedia(string? value, string provider) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && ChatMediaPolicy.IsImageUri(uri)
        && (provider == "GIPHY" ? uri.Host.EndsWith(".giphy.com", StringComparison.Ordinal) : uri.Host is "static.klipy.com" or "static1.klipy.com" or "static2.klipy.com");
}
