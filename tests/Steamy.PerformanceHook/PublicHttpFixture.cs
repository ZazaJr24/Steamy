using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Records/replays public responses for a measurement fixture. No production code
// references this type; unrecognized endpoints never fall back to the live network.
internal static class PublicHttpFixture
{
    private sealed record Entry(string key, string uri, string file, string sha256, int status, string? contentType, string? error = null);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly ConcurrentQueue<string> Missing = new();
    private static readonly ConcurrentQueue<object> Failures = new();
    private static readonly Dictionary<HttpClient, HttpClient> Clients = new(ReferenceEqualityComparer.Instance);
    private static string? _folder;
    private static long _calls;
    internal static bool Recording => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_RECORD_PUBLIC_HTTP"));
    internal static bool Enabled => _folder is not null;
    internal static long Calls => Interlocked.Read(ref _calls);
    internal static void Initialize(string fixture)
    {
        _folder = Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_RECORD_PUBLIC_HTTP");
        if (Recording) Directory.CreateDirectory(_folder!);
        var manifest = Path.Combine(Recording ? _folder! : fixture, "http-manifest.json");
        if (Recording && !File.Exists(manifest))
        {
            var previous = Path.Combine(fixture, "http-manifest.json");
            if (!File.Exists(previous)) return;
            foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllBytes(previous))!)
            {
                if (!Allowed(new Uri(entry.uri)) || entry.file != entry.key + ".bin") throw new InvalidDataException("Unsafe inherited HTTP fixture entry.");
                File.Copy(Path.Combine(fixture, "http", entry.file), Path.Combine(_folder!, entry.file));
            }
            File.Copy(previous, manifest);
        }
        if (!File.Exists(manifest)) { _folder = null; return; }
        if (!Recording) _folder = Path.Combine(fixture, "http");
        foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllBytes(manifest))!)
        {
            if (!Allowed(new Uri(entry.uri)) || entry.file != entry.key + ".bin") throw new InvalidDataException("Unsafe HTTP fixture entry.");
            using var body = File.OpenRead(Path.Combine(_folder!, entry.file));
            if (Convert.ToHexString(SHA256.HashData(body)) != entry.sha256)
                throw new InvalidDataException("HTTP fixture bytes changed.");
            Entries[entry.key] = entry;
        }
    }
    internal static void Attach(IServiceProvider services, Assembly assembly, object spotlight)
    {
        if (!Enabled) return;
        Replace(spotlight, "_http");
        Replace(services.GetService(assembly.GetType("Steamy.Services.ISteamCatalogService")!)!, "_httpClient");
        Replace(services.GetService(assembly.GetType("Steamy.Services.IArtworkService")!)!, "_httpClient");
        Replace(assembly.GetType("Steamy.Services.ImagePipeline")!.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!, "_http");
    }
    private static void Replace(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = (HttpClient)field.GetValue(owner)!;
        if (!Clients.TryGetValue(original, out var client))
        {
            // Let the original client own its timeout during recording so it can
            // be distinguished from navigation cancellation in the handler.
            client = new HttpClient(new Handler(original)) { Timeout = Recording ? Timeout.InfiniteTimeSpan : original.Timeout };
            foreach (var header in original.DefaultRequestHeaders) client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            Clients.Add(original, client);
        }
        field.SetValue(owner, client);
    }
    internal static object? Snapshot()
    {
        if (!Enabled) return null;
        if (Recording) File.WriteAllBytes(Path.Combine(_folder!, "http-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(Entries.Values.OrderBy(entry => entry.key).ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        return new { recording = Recording, requests = Calls, responseCount = Entries.Count, missing = Missing.ToArray(), failures = Failures.ToArray() };
    }
    internal static bool Complete => Enabled && !Recording && Missing.IsEmpty;
    private static string Key(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    private static bool Allowed(Uri uri)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0) return false;
        var path = uri.AbsolutePath;
        var keys = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0])).ToArray();
        if (uri.Host == "api.steampowered.com" && path == "/IStoreBrowseService/GetItems/v1/")
            return keys.All(key => key == "input_json");
        if (uri.Host == "store.steampowered.com" && path.TrimEnd('/') == "/api/appdetails")
            return keys.All(key => key is "appids" or "l" or "cc" or "filters");
        if (uri.Host == "api.steamcmd.net" && path.StartsWith("/v1/info/", StringComparison.Ordinal))
            return keys.Length == 0 && int.TryParse(path[9..].TrimEnd('/'), out var id) && id > 0;
        if (uri.Host == "raw.githubusercontent.com" && path == "/ZazaJr24/Steamy/main/src/Steamy/Data/spotlight.json") return keys.Length == 0;
        return uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("/store_item_assets/steam/apps/", StringComparison.Ordinal) || path.StartsWith("/steam/apps/", StringComparison.Ordinal))
            && (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) && keys.All(key => key == "t");
    }
    private sealed class Handler(HttpClient original) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Get || request.Headers.Authorization is not null || request.Headers.Contains("Cookie") || !Allowed(uri))
            { Missing.Enqueue(uri.Host + uri.AbsolutePath); throw new HttpRequestException("Endpoint is outside the public benchmark fixture."); }
            var key = Key(uri);
            var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Entries.TryGetValue(key, out var entry))
                {
                    if (!Recording) { Missing.Enqueue(uri.AbsoluteUri); throw new HttpRequestException("Unrecorded public fixture response."); }
                    using var clone = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
                    foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    HttpResponseMessage response;
                    try { response = await original.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Preserve a real client timeout as a timeout, never invent a
                        // 404. Caller/navigation cancellations are not fixture data.
                        var empty = Array.Empty<byte>();
                        await File.WriteAllBytesAsync(Path.Combine(_folder!, key + ".bin"), empty, CancellationToken.None).ConfigureAwait(false);
                        entry = new(key, uri.AbsoluteUri, key + ".bin", Convert.ToHexString(SHA256.HashData(empty)), 0, null, "timeout");
                        Entries[key] = entry;
                        throw;
                    }
                    catch (HttpRequestException failure)
                    {
                        var empty = Array.Empty<byte>();
                        await File.WriteAllBytesAsync(Path.Combine(_folder!, key + ".bin"), empty, CancellationToken.None).ConfigureAwait(false);
                        entry = new(key, uri.AbsoluteUri, key + ".bin", Convert.ToHexString(SHA256.HashData(empty)), 0, null, "http:" + failure.HttpRequestError);
                        Entries[key] = entry;
                        throw;
                    }
                    using (response)
                    {
                    if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidDataException("Recorded public response exceeds 16 MiB.");
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                    { if (buffer.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("Recorded public response exceeds 16 MiB."); await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false); }
                    var bytes = buffer.ToArray();
                    await File.WriteAllBytesAsync(Path.Combine(_folder!, key + ".bin"), bytes, cancellationToken).ConfigureAwait(false);
                    entry = new(key, uri.AbsoluteUri, key + ".bin", Convert.ToHexString(SHA256.HashData(bytes)), (int)response.StatusCode, response.Content.Headers.ContentType?.ToString());
                    Entries[key] = entry;
                    }
                }
                if (entry.error == "timeout") throw new TaskCanceledException("Recorded public fixture timeout.", new TimeoutException("Recorded public client timeout."));
                if (entry.error?.StartsWith("http:", StringComparison.Ordinal) == true && Enum.TryParse<HttpRequestError>(entry.error[5..], out var recordedError))
                    throw new HttpRequestException(recordedError, "Recorded public network failure.");
                var result = new HttpResponseMessage((HttpStatusCode)entry.status) { Content = new StreamContent(File.OpenRead(Path.Combine(_folder!, entry.file))), RequestMessage = request };
                if (entry.contentType is not null) result.Content.Headers.TryAddWithoutValidation("Content-Type", entry.contentType);
                return result;
            }
            catch (Exception failure) when (Recording)
            {
                Failures.Enqueue(new { endpoint = uri.Host + uri.AbsolutePath, type = failure.GetType().Name, innerType = failure.InnerException?.GetType().Name, cancelled = cancellationToken.IsCancellationRequested });
                throw;
            }
            finally { gate.Release(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
    }
}
