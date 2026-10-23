using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordBrowserAuthorizationTests
{
    [Fact]
    public async Task ApprovalUsesDiscordPkceAndReturnsOnlyAfterMatchingLoopbackCallback()
    {
        using var fixture = new BrowserFixture();
        var approval = await fixture.Opened.Task;
        Assert.Equal("https", approval.Scheme);
        Assert.Equal("discord.com", approval.Host);
        Assert.Equal("/oauth2/authorize", approval.AbsolutePath);
        var query = Query(approval);
        Assert.Equal("42", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("openid sdk.social_layer", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("test-public-challenge", query["code_challenge"]);
        Assert.Equal(32, Convert.FromHexString(query["state"]).Length);
        Assert.False(fixture.Approval.IsCompleted);
        var callback = Callback(query, "code=fixture-approval-code");
        using var response = await fixture.Http.GetAsync(callback);
        var body = await response.Content.ReadAsStringAsync();
        var result = await fixture.Approval;
        Assert.Equal("fixture-approval-code", result.Code);
        Assert.Equal(query["redirect_uri"], result.Redirect);
        Assert.DoesNotContain("fixture-approval-code", body);
        Assert.DoesNotContain(query["state"], body);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task WrongStateCannotApproveOrCancelARealLogin()
    {
        using var fixture = new BrowserFixture();
        var query = Query(await fixture.Opened.Task);
        using var wrongState = await fixture.Http.GetAsync(query["redirect_uri"] + "?state=wrong&error=access_denied&code=forged-code");
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
        Assert.False(fixture.Approval.IsCompleted);
        using var valid = await fixture.Http.GetAsync(Callback(query, "code=real-fixture-code"));
        Assert.Equal("real-fixture-code", (await fixture.Approval).Code);
    }

    [Fact]
    public async Task DuplicateStateAndForeignHostAreRejectedWhileRealCallbackStillWorks()
    {
        using var fixture = new BrowserFixture();
        var query = Query(await fixture.Opened.Task);
        using var duplicate = await fixture.Http.GetAsync(Callback(query, "state=second&code=forged-code"));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, Callback(query, "code=forged-code"));
        request.Headers.Host = "foreign.example";
        using var foreign = await fixture.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.False(fixture.Approval.IsCompleted);
        using var valid = await fixture.Http.GetAsync(Callback(query, "code=valid-code"));
        Assert.Equal("valid-code", (await fixture.Approval).Code);
    }

    [Fact]
    public async Task DeclinedApprovalNeverLeaksErrorDescriptionsOrProducesACode()
    {
        using var fixture = new BrowserFixture();
        var query = Query(await fixture.Opened.Task);
        using var response = await fixture.Http.GetAsync(Callback(query, "error=access_denied&error_description=private-response-text"));
        var body = await response.Content.ReadAsStringAsync();
        var error = await Assert.ThrowsAsync<DiscordSocialException>(() => fixture.Approval);
        Assert.DoesNotContain("private-response-text", error.Message + body);
        Assert.Contains("declined", error.Message);
    }

    [Fact]
    public async Task CancellingSignInReleasesItsListeningSocket()
    {
        using var fixture = new BrowserFixture();
        var query = Query(await fixture.Opened.Task);
        var port = new Uri(query["redirect_uri"]).Port;
        fixture.Deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Approval);
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SeparateAttemptsHaveDifferentStates()
    {
        using var first = new BrowserFixture(); using var second = new BrowserFixture();
        var firstQuery = Query(await first.Opened.Task);
        var secondQuery = Query(await second.Opened.Task);
        Assert.NotEqual(firstQuery["state"], secondQuery["state"]);
        first.Deadline.Cancel(); second.Deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Approval);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Approval);
    }

    [Fact]
    public async Task CompletedSignInCanBeFollowedByAnotherAttemptOnTheSamePort()
    {
        using var first = new BrowserFixture();
        var firstQuery = Query(await first.Opened.Task);
        using var firstResponse = await first.Http.GetAsync(Callback(firstQuery, "code=first-code"));
        await first.Approval;
        using var second = new BrowserFixture(new Uri(firstQuery["redirect_uri"]).Port);
        Assert.False(second.Approval.IsFaulted);
        var secondQuery = Query(await second.Opened.Task);
        using var secondResponse = await second.Http.GetAsync(Callback(secondQuery, "code=second-code"));
        Assert.Equal("second-code", (await second.Approval).Code);
    }

    private static string Callback(Dictionary<string, string> query, string parameters) => query["redirect_uri"] + "?state=" + Uri.EscapeDataString(query["state"]) + "&" + parameters;
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));
    private sealed class BrowserFixture : IDisposable
    {
        public CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(10));
        public TaskCompletionSource<Uri> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient Http { get; } = new(new SocketsHttpHandler { UseProxy = false });
        public Task<DiscordAuthorizationCode> Approval { get; }
        public BrowserFixture(int port = 0) => Approval = new DiscordBrowserAuthorization(uri => Opened.TrySetResult(uri), port).AuthorizeAsync(42, "openid sdk.social_layer", "test-public-challenge", Deadline.Token);
        public void Dispose() { Deadline.Cancel(); Http.Dispose(); Deadline.Dispose(); }
    }
}
