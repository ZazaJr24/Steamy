using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Steamy.Services;

/// <summary>
/// Builds the HTTP handler the app's network clients use.
/// <para>
/// With a DoH resolver selected in Settings every host name is looked up through that resolver
/// instead of the machine's configured one, the answers are cached, and a failed lookup falls back
/// to the system resolver instead of breaking the request. A single resolver that is slow or
/// unreachable therefore never takes downloads down with it — which is what "stable DNS" means
/// here. Windows' own DNS settings are never touched and no DNS packets leave the process.
/// </para>
/// </summary>
public static class StableDnsHandler
{
    /// <summary>How long a resolved name is reused before it is looked up again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A DoH lookup that takes longer than this is abandoned in favour of the next attempt.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient DohClient = new(new SocketsHttpHandler { ConnectTimeout = LookupTimeout })
    {
        Timeout = LookupTimeout
    };

    private static string _mode = "System resolver";
    private static string _endpoint = string.Empty;

    private sealed record CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTime ExpiresUtc);

    /// <summary>True when host names are resolved through a DoH endpoint.</summary>
    public static bool IsActive => ResolveEndpoint(_mode, _endpoint) is not null;

    /// <summary>Short human readable state for the Settings page.</summary>
    public static string Status => IsActive
        ? $"Stable DNS active · {_mode} · answers cached for 5 min"
        : "Machine DNS in use · pick a DoH resolver for steady lookups";

    /// <summary>Called whenever settings are loaded or saved so the app follows the setting at once.</summary>
    public static void Configure(string? mode, string? endpoint)
    {
        var nextMode = string.IsNullOrWhiteSpace(mode) ? "System resolver" : mode.Trim();
        var nextEndpoint = endpoint?.Trim() ?? string.Empty;
        if (string.Equals(nextMode, _mode, StringComparison.Ordinal)
            && string.Equals(nextEndpoint, _endpoint, StringComparison.Ordinal))
            return;

        _mode = nextMode;
        _endpoint = nextEndpoint;
        Cache.Clear();
    }

    /// <summary>A fresh handler for one client. Handlers are not shareable, so call this per client.</summary>
    public static HttpMessageHandler Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true
        };

        if (IsActive) handler.ConnectCallback = ConnectAsync;
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                socket.Dispose();
                lastError = exception;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>Resolves a host through the selected endpoint, the cache, or the machine as a last resort.</summary>
    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        if (Cache.TryGetValue(host, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Addresses;

        var endpoint = ResolveEndpoint(_mode, _endpoint);
        if (endpoint is not null)
        {
            var resolved = await TryResolveWithDohAsync(endpoint, host, cancellationToken).ConfigureAwait(false);
            if (resolved.Count > 0)
            {
                Cache[host] = new CacheEntry(resolved, DateTime.UtcNow + CacheLifetime);
                return resolved;
            }
        }

        // Never fail because of the resolver: the machine's own lookup answers when DoH cannot.
        var system = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (system.Length > 0) Cache[host] = new CacheEntry(system, DateTime.UtcNow + CacheLifetime);
        return system;
    }

    private static async Task<IReadOnlyList<IPAddress>> TryResolveWithDohAsync(string endpoint, string host, CancellationToken cancellationToken)
    {
        var addresses = new List<IPAddress>();
        foreach (var type in new[] { "A", "AAAA" })
        {
            try
            {
                var url = $"{endpoint}{(endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?')}name={Uri.EscapeDataString(host)}&type={type}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var response = await DohClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in answer.EnumerateArray())
                {
                    if (entry.TryGetProperty("data", out var data)
                        && data.GetString() is { Length: > 0 } value
                        && IPAddress.TryParse(value, out var address))
                    {
                        addresses.Add(address);
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or OperationCanceledException)
            {
                // A resolver that is down or too slow simply hands over to the system resolver.
            }
        }

        return addresses;
    }

    /// <summary>Maps a preset name from Settings to the DoH JSON endpoint it uses.</summary>
    private static string? ResolveEndpoint(string mode, string endpoint) => mode switch
    {
        "Cloudflare DoH" => "https://cloudflare-dns.com/dns-query",
        "Google DoH" => "https://dns.google/resolve",
        "Quad9 DoH" => "https://dns.quad9.net:5053/dns-query",
        "Custom DoH" => string.IsNullOrWhiteSpace(endpoint) ? "https://cloudflare-dns.com/dns-query" : endpoint,
        _ => null
    };
}
