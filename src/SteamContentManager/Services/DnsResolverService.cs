using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace SteamContentManager.Services;

public sealed record DnsDiagnosticResult(
    bool Succeeded,
    string Host,
    string Mode,
    IReadOnlyList<string> Addresses,
    long ElapsedMilliseconds,
    string Message);

public interface IDnsResolverService
{
    Task<DnsDiagnosticResult> DiagnoseAsync(string host, string mode, string endpoint, CancellationToken cancellationToken = default);
}

/// <summary>
/// App-scoped DNS diagnostics. It never changes Windows DNS settings, registry values,
/// adapters or other applications. DoH mode is used only for this diagnostic request.
/// </summary>
public sealed class DnsResolverService : IDnsResolverService
{
    private readonly HttpClient _httpClient;

    public DnsResolverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<DnsDiagnosticResult> DiagnoseAsync(string host, string mode, string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new DnsDiagnosticResult(false, string.Empty, mode, Array.Empty<string>(), 0, "Enter a host name first.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = mode switch
            {
                "Cloudflare DoH" or "Google DoH" or "Quad9 DoH" or "Custom DoH"
                    => await ResolveWithDohAsync(host.Trim(), endpoint, cancellationToken).ConfigureAwait(false),
                _ => (await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken).ConfigureAwait(false))
                    .Select(address => address.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            stopwatch.Stop();
            return new DnsDiagnosticResult(addresses.Count > 0, host.Trim(), mode, addresses, stopwatch.ElapsedMilliseconds,
                addresses.Count > 0 ? "Resolution succeeded. App-only networking is unchanged outside this application." : "No addresses returned.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or TaskCanceledException or UriFormatException)
        {
            stopwatch.Stop();
            return new DnsDiagnosticResult(false, host.Trim(), mode, Array.Empty<string>(), stopwatch.ElapsedMilliseconds, $"DNS diagnostic failed: {exception.GetType().Name}.");
        }
    }

    private async Task<IReadOnlyList<string>> ResolveWithDohAsync(string host, string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new UriFormatException("DoH endpoint must be HTTPS.");

        var requestUri = new UriBuilder(uri)
        {
            Query = $"name={Uri.EscapeDataString(host)}&type=A"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DohResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Answers?
            .Where(answer => answer.Type == 1 && !string.IsNullOrWhiteSpace(answer.Data))
            .Select(answer => answer.Data!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
    }

    private sealed class DohResponse
    {
        [JsonPropertyName("Answer")]
        public List<DohAnswer>? Answers { get; set; }
    }

    private sealed class DohAnswer
    {
        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }
}
