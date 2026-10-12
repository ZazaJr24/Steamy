using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Steamy.Services;

public interface ITrailerPreviewService
{
    Task<Uri?> GetPreviewAsync(int appId, CancellationToken cancellationToken = default);
    async Task<TrailerVideo?> GetVideoPreviewAsync(int appId, CancellationToken cancellationToken = default)
    {
        var uri = await GetPreviewAsync(appId, cancellationToken).ConfigureAwait(false);
        return uri is null ? null : new TrailerVideo(uri, false);
    }
}

public sealed record TrailerVideo(Uri Source, bool IsHdr);

/// <summary>Resolves official Steam trailers and caches a small, silent H.264 preview.</summary>
public sealed class TrailerPreviewService : ITrailerPreviewService, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _gate = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<Uri?>>> _requests = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<Uri?>>> _hdrRequests = new();
    private const int MaxPreviewBytes = 12 * 1024 * 1024;

    public TrailerPreviewService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _http = httpClient ?? new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromSeconds(12) };
        _ownsHttp = httpClient is null;
        _cacheDirectory = cacheDirectory ?? Path.Combine(Steamy.Storage.AppDataPaths.Root, "trailer-previews");
    }

    public async Task<Uri?> GetPreviewAsync(int appId, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var request = _requests.GetOrAdd(appId, id => new Lazy<Task<Uri?>>(() => LoadAsync(id)));
        var result = await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Optional CDN failures can be retried on a later deliberate hover.
        if (result is null) _requests.TryRemove(new KeyValuePair<int, Lazy<Task<Uri?>>>(appId, request));
        return result;
    }

    public async Task<TrailerVideo?> GetVideoPreviewAsync(int appId, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var request = _hdrRequests.GetOrAdd(appId, id => new Lazy<Task<Uri?>>(() => LoadHdrAsync(id)));
        var hdr = await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (hdr is not null) return new(hdr, true);
        var sdr = await GetPreviewAsync(appId, cancellationToken).ConfigureAwait(false);
        return sdr is null ? null : new(sdr, false);
    }

    private async Task<Uri?> LoadHdrAsync(int appId)
    {
        var path = Path.Combine(_cacheDirectory, $"{appId}-preview-hdr-av1-v1.mp4");
        var temp = path + ".tmp";
        var entered = false;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxPreviewBytes
                && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) return new Uri(Path.GetFullPath(path));
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            var bytes = await ReadBoundedAsync(new Uri($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english"), 2 * 1024 * 1024).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes);
            if (!json.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app)
                || !app.TryGetProperty("data", out var data) || !data.TryGetProperty("movies", out var movies)
                || movies.ValueKind != JsonValueKind.Array) return null;
            foreach (var movie in movies.EnumerateArray().OrderByDescending(m => m.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True))
            {
                if (!movie.TryGetProperty("dash_av1", out var value) || !TrySteamUri(value.GetString(), out var manifest)) continue;
                var segments = GetHdrSegments(await ReadBoundedAsync(manifest!, 256 * 1024).ConfigureAwait(false), manifest!);
                if (segments.Count == 0) continue;
                Directory.CreateDirectory(_cacheDirectory);
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    foreach (var segment in segments)
                    {
                        var part = await ReadBoundedAsync(segment, MaxPreviewBytes - (int)output.Length).ConfigureAwait(false);
                        await output.WriteAsync(part, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                File.Move(temp, path, true);
                return new Uri(Path.GetFullPath(path));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or XmlException or InvalidDataException or OperationCanceledException or FormatException or OverflowException or InvalidOperationException) { }
        finally
        {
            if (entered) _gate.Release();
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private async Task<Uri?> LoadAsync(int appId)
    {
        var path = Path.Combine(_cacheDirectory, $"{appId}-preview-v2.mp4");
        var temp = path + ".tmp";
        var entered = false;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxPreviewBytes
                && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) return new Uri(Path.GetFullPath(path));
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            var metadata = await ReadBoundedAsync(new Uri($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english"), 2 * 1024 * 1024).ConfigureAwait(false);
            using var json = JsonDocument.Parse(metadata);
            if (!json.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app)
                || !app.TryGetProperty("data", out var data) || !data.TryGetProperty("movies", out var movies)
                || movies.ValueKind != JsonValueKind.Array) return null;
            var ordered = movies.EnumerateArray().OrderByDescending(movie => movie.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True);
            foreach (var movie in ordered)
            {
                if (movie.TryGetProperty("dash_h264", out var dash) && TrySteamUri(dash.GetString(), out var manifest))
                {
                    var bytes = await ReadBoundedAsync(manifest!, 256 * 1024).ConfigureAwait(false);
                    var segments = GetSegments(bytes, manifest!);
                    if (segments.Count == 0) continue;
                    Directory.CreateDirectory(_cacheDirectory);
                    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        foreach (var segment in segments)
                        {
                            var part = await ReadBoundedAsync(segment, MaxPreviewBytes - (int)output.Length).ConfigureAwait(false);
                            await output.WriteAsync(part, _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    File.Move(temp, path, overwrite: true);
                    return new Uri(Path.GetFullPath(path));
                }
                if (movie.TryGetProperty("mp4", out var mp4) && mp4.ValueKind == JsonValueKind.Object
                    && mp4.TryGetProperty("480", out var legacy) && TrySteamUri(legacy.GetString(), out var direct)) return direct;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or XmlException or InvalidDataException or OperationCanceledException or FormatException or OverflowException or InvalidOperationException) { }
        finally
        {
            if (entered) _gate.Release();
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static bool TrySteamUri(string? value, out Uri? uri)
    {
        uri = null;
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(candidate.UserInfo)
            || !candidate.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)) return false;
        uri = candidate;
        return true;
    }

    public static IReadOnlyList<Uri> GetSegments(byte[] bytes, Uri manifest) => GetSegmentsCore(bytes, manifest, false);
    public static IReadOnlyList<Uri> GetHdrSegments(byte[] bytes, Uri manifest) => GetSegmentsCore(bytes, manifest, true);

    private static bool IsHdrAv1(XElement representation)
    {
        var codec = ((string?)representation.Attribute("codecs"))?.Split('.') ?? [];
        if (codec.Length < 4 || codec[0] != "av01" || !int.TryParse(codec[3], out var depth) || depth < 10) return false;
        if (codec.Length > 7 && codec[7] is "16" or "18") return true; // SMPTE ST 2084 (PQ), ARIB STD-B67 (HLG).
        return representation.Elements().Concat(representation.Parent?.Elements() ?? [])
            .Any(e => ((string?)e.Attribute("schemeIdUri")) == "urn:mpeg:mpegB:cicp:TransferCharacteristics"
                && ((string?)e.Attribute("value")) is "16" or "18");
    }

    private static IReadOnlyList<Uri> GetSegmentsCore(byte[] bytes, Uri manifest, bool hdr)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 });
        var document = XDocument.Load(reader);
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        var representation = document.Descendants(ns + "Representation")
            .Where(element => ((string?)element.Attribute("mimeType")) == "video/mp4"
                && (hdr ? IsHdrAv1(element) : ((string?)element.Attribute("codecs"))?.StartsWith("avc1", StringComparison.Ordinal) == true)
                && int.TryParse((string?)element.Attribute("height"), out var height) && height >= 240 && height <= (hdr ? 2160 : 720))
            .OrderBy(element => Math.Abs((int)element.Attribute("height")! - (hdr ? 720 : 360))).FirstOrDefault();
        var template = representation?.Element(ns + "SegmentTemplate") ?? representation?.Parent?.Element(ns + "SegmentTemplate");
        var id = (string?)representation?.Attribute("id");
        if (template is null || id is null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,32}$")) return Array.Empty<Uri>();
        var initialization = (string?)template.Attribute("initialization");
        var media = (string?)template.Attribute("media");
        if (initialization is null || media is null || initialization.Length > 2048 || media.Length > 2048
            || !double.TryParse((string?)template.Attribute("duration"), CultureInfo.InvariantCulture, out var duration)
            || !double.TryParse((string?)template.Attribute("timescale") ?? "1", CultureInfo.InvariantCulture, out var timescale)
            || duration <= 0 || timescale <= 0) return Array.Empty<Uri>();
        var start = int.TryParse((string?)template.Attribute("startNumber"), out var number) ? number : 1;
        var seconds = 24d;
        if (document.Root?.Attribute("mediaPresentationDuration") is { } total) seconds = Math.Min(seconds, XmlConvert.ToTimeSpan(total.Value).TotalSeconds);
        var count = Math.Clamp((int)Math.Ceiling(seconds * timescale / duration), 1, 12);
        var paths = new List<string> { initialization.Replace("$RepresentationID$", id, StringComparison.Ordinal) };
        for (var index = 0; index < count; index++)
        {
            var value = checked(start + index);
            paths.Add(Regex.Replace(media.Replace("$RepresentationID$", id, StringComparison.Ordinal), @"\$Number(?:%0([1-8])d)?\$",
                match => value.ToString(match.Groups[1].Success ? "D" + match.Groups[1].Value : "D", CultureInfo.InvariantCulture)));
        }
        var urls = new List<Uri>();
        foreach (var path in paths)
        {
            if (path.Contains('$') || !Uri.TryCreate(manifest, path, out var resolved) || !TrySteamUri(resolved.AbsoluteUri, out var uri)) return Array.Empty<Uri>();
            urls.Add(uri!);
        }
        return urls;
    }

    private async Task<byte[]> ReadBoundedAsync(Uri uri, int limit)
    {
        if (limit <= 0) throw new InvalidDataException("Trailer preview is too large.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Trailer response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Trailer response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
