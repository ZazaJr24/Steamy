using System.Diagnostics;
using System.Net.Http;

namespace Steamy.Services;

/// <summary>What a reachability check found. A failure carries no exception, only a plain message.</summary>
public sealed record EndpointProbeResult(
    bool Reachable,
    int? StatusCode,
    long ElapsedMilliseconds,
    string Message);

public interface IEndpointProbeService
{
    /// <summary>
    /// Calls the configured address and reports what really came back. Nothing is sent along: no
    /// API key, no account name, no query parameters — the request only asks whether the endpoint
    /// answers at all.
    /// </summary>
    Task<EndpointProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class HttpEndpointProbeService : IEndpointProbeService
{
    private readonly HttpClient _client;

    public HttpEndpointProbeService(HttpClient? client = null)
        => _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<EndpointProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new EndpointProbeResult(false, null, 0, "No address is configured.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return new EndpointProbeResult(false, null, 0, "That address is not a valid URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return new EndpointProbeResult(false, null, 0, "Only http and https addresses can be checked.");

        // Credentials in the URL would end up in logs and proxies; the app never uses them.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new EndpointProbeResult(false, null, 0, "Remove the user information from the URL; credentials do not belong there.");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            // Any HTTP answer proves the endpoint is reachable; the status itself is reported
            // unchanged so a 401 or 403 is not silently turned into a success.
            var message = $"{uri.Host} answered with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
            return new EndpointProbeResult(true, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(false, null, stopwatch.ElapsedMilliseconds, $"{uri.Host} did not answer within the timeout.");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(false, null, stopwatch.ElapsedMilliseconds, $"{uri.Host} could not be reached ({exception.Message}).");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(false, null, stopwatch.ElapsedMilliseconds, $"{uri.Host} could not be checked ({exception.GetType().Name}).");
        }
    }
}
