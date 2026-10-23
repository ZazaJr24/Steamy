using System.Text.Json;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class DiscordSocialTests
{
    [Fact]
    public async Task UnconfiguredOrSkippedDiscordNeverReadsCredentialsOrCreatesAClient()
    {
        var settings = new Settings { Value = new() { DiscordApplicationId = "" } };
        var credentials = new Credentials();
        var created = 0;
        using var service = new DiscordSocialService(settings, credentials, () => { created++; return new Client(); }, () => false);
        await service.RestoreAsync();
        Assert.False(service.IsAvailable);
        Assert.Equal(0, created);
        Assert.Equal(0, credentials.Reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync());
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ConnectStoresOnlyTheSessionInTheCredentialStoreAndWorksWithoutSteam()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Settings.Value.SteamUsername);
        await fixture.Service.ConnectAsync();
        Assert.True(fixture.Service.Snapshot.IsConnected);
        Assert.True(fixture.Service.HasSavedSession);
        Assert.Equal(DiscordSocialService.SessionCredentialKey, fixture.Credentials.LastKey);
        var session = JsonSerializer.Deserialize<DiscordSession>(fixture.Credentials.Value!)!;
        Assert.Equal(42UL, session.ApplicationId);
        Assert.Equal("access", session.AccessToken);
        Assert.Equal("refresh", session.RefreshToken);
        Assert.DoesNotContain("access", JsonSerializer.Serialize(fixture.Settings.Value));
        Assert.DoesNotContain("\"refresh\"", JsonSerializer.Serialize(fixture.Settings.Value));
        Assert.Equal(0, fixture.Settings.Saves);
    }

    [Fact]
    public async Task RestoreDoesNotPromptAndOnlyRunsOnceForTheSameSavedSession()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Value = JsonSerializer.Serialize(Session());
        await fixture.Service.RestoreAsync();
        await fixture.Service.RestoreAsync();
        Assert.Equal(0, fixture.Client.Authorizations);
        Assert.Equal(1, fixture.Client.Connections);
        Assert.Equal(42UL, fixture.Client.ConnectedApplicationId);
        Assert.Equal(1, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Client.Refreshes);
    }

    [Fact]
    public async Task ExpiredSessionRefreshesAndSavesTheRotatedTokensBeforeConnecting()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Value = JsonSerializer.Serialize(Session() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await fixture.Service.RestoreAsync();
        Assert.Equal(1, fixture.Client.Refreshes);
        Assert.Equal("rotated-access", fixture.Client.ConnectedToken);
        Assert.Contains("rotated-refresh", fixture.Credentials.Value);
    }

    [Fact]
    public async Task ExpirationDuringAnActiveConnectionRotatesTheStoredSession()
    {
        using var fixture = new Fixture();
        await fixture.Service.ConnectAsync();
        fixture.Client.ExpireToken();
        Assert.Equal(1, fixture.Client.Refreshes);
        Assert.Equal("rotated-access", fixture.Client.ConnectedToken);
        Assert.Contains("rotated-refresh", fixture.Credentials.Value);
    }

    [Theory]
    [InlineData("corrupt json")]
    [InlineData("{\"ApplicationId\":99,\"AccessToken\":\"old-access\",\"RefreshToken\":\"old-refresh\",\"ExpiresAt\":\"2099-01-01T00:00:00Z\"}")]
    public async Task InvalidOrDifferentApplicationSessionIsDeletedWithoutUsingTheSdk(string saved)
    {
        using var fixture = new Fixture();
        fixture.Credentials.Value = saved;
        await fixture.Service.RestoreAsync();
        Assert.Null(fixture.Credentials.Value);
        Assert.Equal(0, fixture.Created);
        Assert.False(fixture.Service.HasSavedSession);
    }

    [Fact]
    public async Task RevokedAuthorizationClearsCredentialsButANetworkFailureKeepsThem()
    {
        using var revoked = new Fixture();
        revoked.Credentials.Value = JsonSerializer.Serialize(Session() with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        revoked.Client.RefreshError = new DiscordAuthorizationException("Expired");
        await Assert.ThrowsAsync<DiscordAuthorizationException>(() => revoked.Service.RestoreAsync());
        Assert.Null(revoked.Credentials.Value);
        Assert.False(revoked.Service.HasSavedSession);
        Assert.True(revoked.Client.Disposed);

        using var offline = new Fixture();
        offline.Credentials.Value = JsonSerializer.Serialize(Session());
        offline.Client.ConnectError = new DiscordSocialException("Network unavailable");
        await Assert.ThrowsAsync<DiscordSocialException>(() => offline.Service.RestoreAsync());
        Assert.NotNull(offline.Credentials.Value);
        Assert.True(offline.Service.HasSavedSession);
        Assert.True(offline.Client.Disposed);
    }

    [Fact]
    public async Task CancellingAuthorizationLeavesNoSessionAndReleasesTheSdk()
    {
        using var fixture = new Fixture();
        fixture.Client.WaitForAuthorization = true;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.ConnectAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(fixture.Credentials.Value);
        Assert.True(fixture.Client.Disposed);
        Assert.False(fixture.Service.Snapshot.IsConnected);
    }

    [Fact]
    public async Task OfflineSignOutStillDeletesTheCredentialAndDisposesTheConnection()
    {
        using var fixture = new Fixture();
        await fixture.Service.ConnectAsync();
        fixture.Client.RevokeError = new DiscordSocialException("Network unavailable");
        await fixture.Service.DisconnectAsync();
        Assert.Null(fixture.Credentials.Value);
        Assert.False(fixture.Service.HasSavedSession);
        Assert.False(fixture.Service.Snapshot.IsConnected);
        Assert.True(fixture.Client.Disposed);
    }

    [Fact]
    public async Task FriendActionsRequireAConnectionAndRespectRequestDirection()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendFriendRequestAsync("someone"));
        await fixture.Service.ConnectAsync();
        await fixture.Service.SendFriendRequestAsync(" @someone ");
        Assert.Equal("someone", fixture.Client.Request);
        var outgoing = Friend(DiscordRelationshipKind.PendingOutgoing);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UpdateFriendAsync(outgoing, DiscordFriendAction.Accept));
        Assert.Null(fixture.Client.LastAction);
        await fixture.Service.UpdateFriendAsync(outgoing, DiscordFriendAction.Cancel);
        Assert.Equal((7UL, DiscordFriendAction.Cancel), fixture.Client.LastAction);
    }

    [Fact]
    public async Task FriendsFilterKeepsIncomingOutgoingAndBlockedUsersSeparateAndFailedRequestsRemainEditable()
    {
        using var fixture = new Fixture();
        await fixture.Service.ConnectAsync();
        fixture.Client.Snapshot = new(true, new(1, "me", "Me", null),
            [Friend(DiscordRelationshipKind.Friend), Friend(DiscordRelationshipKind.PendingIncoming),
                Friend(DiscordRelationshipKind.PendingOutgoing), Friend(DiscordRelationshipKind.Blocked)]);
        using var model = new DiscordFriendsViewModel(fixture.Service, fixture.Settings);
        Assert.Single(model.Friends);
        model.SelectedFilter = "Requests";
        Assert.Equal(2, model.Friends.Count);
        model.SearchText = "missing";
        Assert.Empty(model.Friends);
        model.SearchText = ""; model.SelectedFilter = "Blocked";
        Assert.True(Assert.Single(model.Friends).IsBlocked);
        fixture.Client.FriendError = new DiscordSocialException("Request failed");
        model.NewFriend = "another.person";
        await model.SendRequestCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        Assert.Equal("another.person", model.NewFriend);
        Assert.Equal("Request failed", model.StatusMessage);
        model.NewFriend = "me";
        await model.SendRequestCommand.ExecuteAsync(null);
        Assert.Contains("own account", model.StatusMessage);
    }

    private static DiscordSession Session() => new(42, "access", "refresh", DateTimeOffset.UtcNow.AddHours(1));
    [Fact]
    public async Task StartupRestoreIsSharedAndNeverRequestsNewAuthorization()
    {
        using var fixture = new Fixture(); fixture.Credentials.Value = JsonSerializer.Serialize(Session());
        using var model = new Steamy.ViewModels.DiscordFriendsViewModel(fixture.Service, fixture.Settings);
        await Task.WhenAll(model.InitializeAsync(), model.InitializeAsync());
        Assert.Equal(0, fixture.Client.Authorizations); Assert.Equal(1, fixture.Client.Connections); Assert.Equal(1, fixture.Credentials.Reads);
    }
    [Fact]
    public async Task APreviouslyRestoredSessionReconnectsAfterConnectionLossWithoutAnotherApproval()
    {
        using var fixture = new Fixture(); fixture.Credentials.Value = JsonSerializer.Serialize(Session());
        await fixture.Service.RestoreAsync();
        fixture.Client.DropConnection();
        await fixture.Service.RestoreAsync();
        Assert.Equal(2, fixture.Client.Connections); Assert.Equal(0, fixture.Client.Authorizations);
        Assert.True(fixture.Service.Snapshot.IsConnected);
        await fixture.Service.DisconnectAsync();
        var readsBeforeSignedOutRestore = fixture.Credentials.Reads;
        await fixture.Service.RestoreAsync();
        Assert.Equal(readsBeforeSignedOutRestore, fixture.Credentials.Reads);
        Assert.Equal(2, fixture.Client.Connections); Assert.False(fixture.Service.HasSavedSession);
    }
    [Fact]
    public async Task StartupDisabledNeverReadsCredentialsAndTransientFailuresCanReconnectWithoutLogin()
    {
        using var fixture = new Fixture(); fixture.Credentials.Value = JsonSerializer.Serialize(Session());
        fixture.Settings.Value.DiscordAutoConnect = false;
        using (var disabled = new Steamy.ViewModels.DiscordFriendsViewModel(fixture.Service, fixture.Settings)) await disabled.InitializeAsync();
        Assert.Equal(0, fixture.Credentials.Reads);
        fixture.Settings.Value.DiscordAutoConnect = true; fixture.Client.ConnectError = new DiscordSocialException("Offline");
        using var model = new Steamy.ViewModels.DiscordFriendsViewModel(fixture.Service, fixture.Settings);
        var delayed = 0;
        model.ReconnectDelay = (_, _) => { delayed++; fixture.Client.ConnectError = null; return Task.CompletedTask; };
        await model.InitializeAsync();
        Assert.Equal(1, delayed); Assert.Equal(2, fixture.Client.Connections); Assert.Equal(0, fixture.Client.Authorizations); Assert.True(model.IsConnected);
    }
    private static DiscordFriend Friend(DiscordRelationshipKind kind) => new(new(5UL + (ulong)kind, "friend", "A Friend", null), kind, "Online");
    private sealed class Fixture : IDisposable
    {
        public Settings Settings { get; } = new();
        public Credentials Credentials { get; } = new();
        public Client Client { get; } = new();
        public DiscordSocialService Service { get; }
        public int Created;
        public Fixture() { Service = new(Settings, Credentials, () => { Created++; return Client; }, () => true); }
        public void Dispose() => Service.Dispose();
    }
    private sealed class Settings : ISettingsService
    {
        public AppSettings Value = new() { DiscordApplicationId = "42" };
        public int Saves;
        public AppSettings Load() => Value;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Credentials : ISecureCredentialService
    {
        public string? Value; public string? LastKey; public int Reads;
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(Value); }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { LastKey = key; Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Client : IDiscordSocialClient
    {
        public DiscordSnapshot Snapshot { get; set; } = DiscordSnapshot.Disconnected;
        public event EventHandler? Changed;
        public event EventHandler? TokenExpired;
        public int Authorizations, Refreshes, Connections;
        public bool Disposed, WaitForAuthorization;
        public Exception? ConnectError, RefreshError, RevokeError, FriendError;
        public string? ConnectedToken, Request;
        public (ulong, DiscordFriendAction)? LastAction;
        public async Task<DiscordSession> AuthorizeAsync(ulong applicationId, CancellationToken token)
        { Authorizations++; if (WaitForAuthorization) await Task.Delay(Timeout.InfiniteTimeSpan, token); return Session(); }
        public Task<DiscordSession> RefreshAsync(DiscordSession session, CancellationToken token)
        { Refreshes++; if (RefreshError is not null) throw RefreshError; return Task.FromResult(session with { AccessToken = "rotated-access", RefreshToken = "rotated-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }); }
        public ulong ConnectedApplicationId;
        public Task ConnectAsync(ulong applicationId, string accessToken, CancellationToken token)
        { Connections++; ConnectedApplicationId = applicationId; ConnectedToken = accessToken; if (ConnectError is not null) throw ConnectError; Snapshot = Snapshot with { IsConnected = true, Profile = new(1, "me", "Me", null) }; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task RevokeAsync(DiscordSession session, CancellationToken token)
        { if (RevokeError is not null) throw RevokeError; return Task.CompletedTask; }
        public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token)
        { if (FriendError is not null) throw FriendError; Request = usernameOrId; return Task.CompletedTask; }
        public Task UpdateFriendAsync(ulong userId, DiscordFriendAction action, CancellationToken token)
        { LastAction = (userId, action); return Task.CompletedTask; }
        public void ExpireToken() => TokenExpired?.Invoke(this, EventArgs.Empty);
        public void DropConnection() { Snapshot = Snapshot with { IsConnected = false }; Changed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Disposed = true; Snapshot = DiscordSnapshot.Disconnected; }
    }
}
