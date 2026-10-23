using System.Text.Json;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordConcurrencyTests
{
    [Fact]
    public async Task SlowHistoryDoesNotDelaySendingAndDisconnectCancelsOutstandingReads()
    {
        var client = new Client(); var credentials = new Credentials();
        using var service = new DiscordSocialService(new Settings(), credentials, () => client, () => true);
        await service.ConnectAsync();
        var history = service.GetMessagesAsync(2, false);
        var sent = service.SendMessageAsync(2, false, "Respond immediately");
        var winner = await Task.WhenAny(sent, Task.Delay(1000));
        if (winner != sent) client.History.TrySetResult([]);
        Assert.Same(sent, winner); Assert.Equal("Respond immediately", (await sent).Content);
        Assert.False(history.IsCompleted);
        await service.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history);
        Assert.Null(credentials.Value);
    }
    private sealed class Settings : ISettingsService
    {
        private readonly AppSettings _value = new() { DiscordApplicationId = "42" };
        public AppSettings Load() => _value;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Credentials : ISecureCredentialService
    {
        public string? Value;
        public Task<string?> ReadAsync(string key, CancellationToken token = default) => Task.FromResult(Value);
        public Task SaveAsync(string key, string value, CancellationToken token = default) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken token = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Client : IDiscordSocialClient, IDiscordCommunicationClient
    {
        public DiscordSnapshot Snapshot { get; private set; } = DiscordSnapshot.Disconnected;
        public DiscordParty? Party => null;
        public IReadOnlyList<DiscordPartyInvite> Invites => [];
        public event EventHandler? Changed;
        public event EventHandler? TokenExpired { add { } remove { } }
        public event EventHandler<DiscordMessageChange>? MessageChanged { add { } remove { } }
        public TaskCompletionSource<IReadOnlyList<DiscordChatMessage>> History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DiscordSession> AuthorizeAsync(ulong id, CancellationToken token) => Task.FromResult(new DiscordSession(id, "fixture-access", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "openid sdk.social_layer"));
        public Task<DiscordSession> RefreshAsync(DiscordSession session, CancellationToken token) => Task.FromResult(session);
        public Task ConnectAsync(ulong id, string accessToken, CancellationToken token) { Snapshot = new(true, new(1, "me", "Me", null), [new(new(2, "friend", "Friend", null), DiscordRelationshipKind.Friend, "Online")]); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task RevokeAsync(DiscordSession session, CancellationToken token) => Task.CompletedTask;
        public Task SendFriendRequestAsync(string name, CancellationToken token) => Task.CompletedTask;
        public Task UpdateFriendAsync(ulong id, DiscordFriendAction action, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong id, bool party, CancellationToken token) => History.Task.WaitAsync(token);
        public Task<DiscordChatMessage> SendMessageAsync(ulong id, bool party, string content, CancellationToken token) => Task.FromResult(new DiscordChatMessage(10, 1, id, 0, 3, content, DateTimeOffset.UtcNow, true, false, false));
        public void SetShowingChat(bool showing) { }
        public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token) => Task.CompletedTask;
        public Task InviteToPartyAsync(ulong id, CancellationToken token) => Task.CompletedTask;
        public Task AcceptPartyInviteAsync(Guid id, CancellationToken token) => Task.CompletedTask;
        public void DismissPartyInvite(Guid id) { }
        public Task LeavePartyAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() => Snapshot = DiscordSnapshot.Disconnected;
    }
}
