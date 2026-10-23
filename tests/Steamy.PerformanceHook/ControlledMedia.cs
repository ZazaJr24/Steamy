using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

// Optional measurement fixture only. This assembly is never part of the app package.
internal static class ControlledMedia
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly ConcurrentQueue<Request> Requests = new();
    private static HttpEvents? _events;
    private static IDisposable? _diagnostics;
    private static readonly ConcurrentQueue<object> MetadataRequests = new();
    private static object? _spotlight;
    private static long _starts, _active, _begin, _fixtureBegin;
    private static string? _fixtureId, _expectedIds, _workloadHash, _error;
    private static double _settleMs;
    internal static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_MEDIA_FIXTURE"));
    private sealed record Request(long number, string host, string? publicArtworkPath, string? publicMetadataEndpoint, int[] appIds);
    internal static void Initialize()
    {
        if (!Enabled) return;
        var folder = Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_MEDIA_FIXTURE")!;
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
        _fixtureId = manifest.RootElement.GetProperty("fixtureId").GetString();
        using var feed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "spotlight.json")));
        _expectedIds = Hash(feed.RootElement.GetProperty("games").EnumerateArray().Select(game => game.GetProperty("appId").GetInt32()).ToArray());
        PublicHttpFixture.Initialize(folder);
        _events = new HttpEvents();
        _diagnostics = DiagnosticListener.AllListeners.Subscribe(new PublicMetadataObserver());
    }
    internal static void WindowLoaded(Window window)
    {
        if (!Enabled) return;
        try
        {
            var assembly = window.GetType().Assembly;
            var services = (IServiceProvider)assembly.GetType("Steamy.App")!.GetProperty("Services", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
            _spotlight = services.GetService(assembly.GetType("Steamy.Services.ISpotlightService")!)!;
            // Simulate the ordinary recently-refreshed cache state. The fixed feed
            // was seeded before process launch; do not change product registration.
            _spotlight.GetType().GetField("_lastAttempt", Any)!.SetValue(_spotlight, DateTimeOffset.UtcNow);
            PublicHttpFixture.Attach(services, assembly, _spotlight);
            VerifyFeed();
        }
        catch (Exception error) { _error = error.ToString(); }
    }
    internal static async Task SettleAsync()
    {
        if (!Enabled) return;
        if (_error is not null || _spotlight is null) throw new InvalidOperationException("Controlled media setup failed: " + _error);
        var began = Stopwatch.GetTimestamp();
        if (_spotlight.GetType().GetField("_refresh", Any)?.GetValue(_spotlight) is Task refresh)
            await refresh.WaitAsync(TimeSpan.FromSeconds(15));
        while (Interlocked.Read(ref _active) != 0)
        {
            if (Stopwatch.GetElapsedTime(began).TotalSeconds > 15) throw new TimeoutException("Controlled startup HTTP work did not settle.");
            await Task.Delay(25);
        }
        _settleMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        VerifyFeed();
    }
    internal static void Begin(IEnumerable? games = null)
    {
        if (!Enabled) return;
        if (games is not null)
            _workloadHash = Hash(games.Cast<object>().Select(game => new
            {
                id = Property(game, "AppId"), name = Property(game, "Name"), state = Property(game, "InstallState")?.ToString()
            }).ToArray());
        if (Interlocked.Read(ref _active) != 0 && !PublicHttpFixture.Recording) throw new InvalidOperationException("HTTP work is still active at the controlled stress boundary.");
        _begin = Interlocked.Read(ref _starts);
        _fixtureBegin = PublicHttpFixture.Calls;
    }
    internal static async Task DrainRecordingAsync()
    {
        if (!PublicHttpFixture.Recording) return;
        // Recording is setup, never a performance sample. Keep the first view
        // alive long enough to capture its real fallback responses before
        // navigation cancels the requests. Replay uses no artificial delay.
        var deadline = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var starts = Interlocked.Read(ref _starts);
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(50);
            var current = Interlocked.Read(ref _starts);
            if (Interlocked.Read(ref _active) != 0 || current != starts) quiet.Restart();
            starts = current;
            if (quiet.Elapsed >= TimeSpan.FromMilliseconds(500)) return;
        }
    }
    internal static object? Finish(object dashboard)
    {
        if (!Enabled) return null;
        VerifyFeed();
        var end = Interlocked.Read(ref _starts);
        var replayed = PublicHttpFixture.Calls - _fixtureBegin;
        var fixtureHttp = PublicHttpFixture.Snapshot();
        var discoveries = ((IEnumerable)Property(dashboard, "DiscoverGames")!).Cast<object>()
            .Select(feature => Property(Property(feature, "Game")!, "AppId")).ToArray();
        return new
        {
            fixtureId = _fixtureId, feedIdsHash = _expectedIds, workloadHash = _workloadHash,
            discoveryIdsHash = Hash(discoveries), discoveryCount = discoveries.Length, startupHttpSettleMs = _settleMs,
            startupRequests = Requests.Where(request => request.number <= _begin).ToArray(),
            stressRequests = Requests.Where(request => request.number > _begin && request.number <= end).ToArray(),
            publicMetadataRequests = MetadataRequests.ToArray(),
            stressRequestStarts = end - _begin, activeRequestsAtEnd = Interlocked.Read(ref _active),
            fixtureHttp, replayedStressRequests = replayed, liveStressRequests = end - _begin - replayed,
            controlled = (PublicHttpFixture.Enabled ? PublicHttpFixture.Complete && end - _begin == replayed : end == _begin) && Interlocked.Read(ref _active) == 0,
            scope = "Identical pinned public feed and local caches; optional recorded public HTTP responses. Real Steam startup scan is retained. Only the service's last-refresh time is set before page navigation; startup HTTP work is observed and awaited separately. All HTTP requests during the comparable stress stage must be served by the recorded fixture; unrecorded endpoints invalidate the run. Recording runs are excluded. The separate live network-image probe follows this checkpoint."
        };
    }
    private static void VerifyFeed()
    {
        var snapshot = Property(_spotlight!, "Cached")!;
        var games = ((IEnumerable)Property(snapshot, "Games")!).Cast<object>().ToArray();
        if (Hash(games.Select(game => Property(game, "AppId")).ToArray()) != _expectedIds)
            throw new InvalidOperationException("The loaded Spotlight IDs differ from the fixed fixture.");
    }
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Any)!.GetValue(owner);
    private static string Hash(object data) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    internal static object? Diagnostic() => !Enabled ? null : new { fixtureId = _fixtureId, activeRequests = Interlocked.Read(ref _active), requests = Requests.ToArray(), publicMetadataRequests = MetadataRequests.ToArray(), setupError = _error };
    private sealed class PublicMetadataObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly List<IDisposable> _subscriptions = [];
        public void OnNext(DiagnosticListener source)
        { if (source.Name == "HttpHandlerDiagnosticListener") _subscriptions.Add(source.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Key != "System.Net.Http.HttpRequestOut.Start" || item.Value?.GetType().GetProperty("Request")?.GetValue(item.Value) is not HttpRequestMessage { RequestUri: { } uri }) return;
            var endpoint = uri.Host == "store.steampowered.com" && uri.AbsolutePath.TrimEnd('/') == "/api/appdetails"
                || uri.Host == "api.steampowered.com" && uri.AbsolutePath == "/IStoreBrowseService/GetItems/v1/" ? uri.AbsolutePath : null;
            if (endpoint is null) return; // Never retain general URLs, keys or account queries.
            var ids = new List<int>();
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var fields = pair.Split('=', 2);
                if (fields.Length != 2) continue;
                if (fields[0] == "appids" && int.TryParse(fields[1], out var id)) ids.Add(id);
                if (fields[0] != "input_json") continue;
                try
                {
                    using var json = JsonDocument.Parse(Uri.UnescapeDataString(fields[1]));
                    if (json.RootElement.TryGetProperty("ids", out var list))
                        foreach (var row in list.EnumerateArray()) if (row.TryGetProperty("appid", out var value) && value.TryGetInt32(out id)) ids.Add(id);
                }
                catch (JsonException) { }
            }
            MetadataRequests.Enqueue(new { endpoint, appIds = ids.ToArray() });
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
    private sealed class HttpEvents : EventListener
    {
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "System.Net.Http") EnableEvents(source, EventLevel.Informational, EventKeywords.All); }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventName == "RequestStop") { Interlocked.Decrement(ref _active); return; }
            if (args.EventName != "RequestStart") return;
            Interlocked.Increment(ref _active);
            var number = Interlocked.Increment(ref _starts);
            string host = "unknown", path = "";
            for (var i = 0; i < (args.PayloadNames?.Count ?? 0); i++)
            {
                if (args.PayloadNames![i] == "host") host = args.Payload![i]?.ToString() ?? "unknown";
                if (args.PayloadNames[i] == "pathAndQuery") path = args.Payload![i]?.ToString() ?? "";
            }
            var pathOnly = path.Split('?')[0];
            var publicPath = (host.EndsWith("steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))
                && (pathOnly.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || pathOnly.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ? pathOnly : null;
            var endpoint = host == "store.steampowered.com" && pathOnly.TrimEnd('/') == "/api/appdetails"
                || host == "api.steampowered.com" && pathOnly == "/IStoreBrowseService/GetItems/v1/" ? pathOnly : null;
            var ids = new List<int>();
            if (endpoint is not null)
            {
                foreach (var pair in path[(path.IndexOf('?') + 1)..].Split('&'))
                {
                    var fields = pair.Split('=', 2);
                    if (fields.Length != 2) continue;
                    if (fields[0] == "appids" && int.TryParse(fields[1], out var id)) ids.Add(id);
                    if (fields[0] != "input_json") continue;
                    try
                    {
                        using var json = JsonDocument.Parse(Uri.UnescapeDataString(fields[1]));
                        if (json.RootElement.TryGetProperty("ids", out var list))
                            foreach (var row in list.EnumerateArray()) if (row.TryGetProperty("appid", out var value) && value.TryGetInt32(out id)) ids.Add(id);
                    }
                    catch (JsonException) { }
                }
            }
            Requests.Enqueue(new(number, host, publicPath, endpoint, ids.ToArray()));
        }
    }
}
