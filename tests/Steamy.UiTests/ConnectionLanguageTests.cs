using System.Net;
using System.Net.Http;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ConnectionLanguageTests
{
    [Fact]
    public async Task LocalizedWindowsErrorsDoNotLeakIntoConnectionStatus()
    {
        using var http = new HttpClient(new Handler(() => throw new HttpRequestException(
            HttpRequestError.NameResolutionError, "Der angegebene Host ist unbekannt.")));
        var result = await new HttpEndpointProbeService(http).ProbeAsync("https://example.com");
        Assert.False(result.Reachable);
        Assert.Equal("example.com could not be reached (NameResolutionError).", result.Message);
    }

    [Fact]
    public async Task ServerReasonPhrasesDoNotChangeTheInterfaceLanguage()
    {
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        { ReasonPhrase = "Zugriff verweigert" }));
        var result = await new HttpEndpointProbeService(http).ProbeAsync("https://example.com");
        Assert.True(result.Reachable);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("example.com answered with HTTP 403.", result.Message);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(response());
    }
}
