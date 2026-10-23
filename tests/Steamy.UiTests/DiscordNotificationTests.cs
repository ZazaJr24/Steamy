using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class DiscordNotificationTests
{
    [Fact]
    public void DuplicateAndEditedMessagesDoNotCreateRepeatedNotifications()
    {
        var settings = new Settings(); using var center = new NotificationCenterViewModel(settings, action => action());
        var service = new Social(); using var model = new DiscordFriendsViewModel(service, settings, notifications: center);
        service.Emit(Message(50)); service.Emit(Message(50)); service.Emit(Message(50) with { IsEdited = true });
        Assert.Single(center.Items); Assert.Single(center.Toasts); Assert.Equal(1, center.UnreadCount);
        service.Emit(Message(51)); Assert.Single(center.Items); Assert.Equal(2, center.ConversationUnreadCount(2));
    }
    [Fact]
    public void CurrentActiveConversationSuppressesToastsAndMarksEarlierMessagesRead()
    {
        var settings = new Settings(); using var center = new NotificationCenterViewModel(settings, action => action());
        var service = new Social(); using var model = new DiscordFriendsViewModel(service, settings, notifications: center);
        service.Emit(Message(50)); Assert.Equal(1, center.UnreadCount);
        model.SetChatVisible(true); model.SetWindowActive(true); model.SelectedFriend = model.Friends[0];
        Assert.Equal(0, center.UnreadCount); Assert.Empty(center.Toasts);
        service.Emit(Message(51)); Assert.Single(center.Items);
        model.SetWindowActive(false); service.Emit(Message(52)); Assert.Equal(1, center.UnreadCount); Assert.Single(center.Toasts);
    }
    [Fact]
    public void OpeningANotificationKeepsItsTargetAndClearsItsToast()
    {
        using var center = new NotificationCenterViewModel(new Settings(), action => action());
        var id = Guid.NewGuid(); center.Publish(new("invite:1", SteamyNotificationKind.PartyInvite, "Two", "Join a game", 2, InviteId: id));
        SteamyNotification? opened = null; center.OpenRequested += (_, item) => opened = item;
        center.OpenCommand.Execute(center.Items[0]);
        Assert.Equal(id, opened!.Content.InviteId); Assert.Equal(2UL, opened.Content.PeerId);
        Assert.Equal(0, center.UnreadCount); Assert.Empty(center.Toasts);
    }
    [Fact]
    public void NotificationPreferenceBoundsAndExpiryAreRespected()
    {
        var settings = new Settings(); using var center = new NotificationCenterViewModel(settings, action => action());
        settings.Value.Notifications = false; center.Publish(new("muted", SteamyNotificationKind.Information, "Title", "Body")); Assert.Empty(center.Items);
        settings.Value.Notifications = true;
        for (var i = 0; i < 100; i++) center.Publish(new("info:" + i, SteamyNotificationKind.Information, new string('t', 300), new string('b', 1000)));
        Assert.Equal(80, center.Items.Count); Assert.Equal(3, center.Toasts.Count); Assert.All(center.Items, item => Assert.True(item.Body.Length <= 161));
        center.ExpireToasts(DateTimeOffset.UtcNow.AddMinutes(1)); Assert.Empty(center.Toasts); Assert.Equal(80, center.UnreadCount);
        center.MarkAllReadCommand.Execute(null); Assert.Equal(0, center.UnreadCount);
    }
    [Fact]
    public async Task NotificationsClearSocialContentOnSignOutAndDoNotNotifyFetchedHistory()
    {
        var settings = new Settings(); using var center = new NotificationCenterViewModel(settings, action => action());
        var service = new Social(); using var model = new DiscordFriendsViewModel(service, settings, notifications: center);
        service.History = [Message(1) with { SentAt = DateTimeOffset.UtcNow.AddDays(-1) }];
        model.SelectedFriend = model.Friends[0]; Assert.Empty(center.Items);
        service.Emit(Message(2)); center.Publish(new("download", SteamyNotificationKind.Download, "Ready", "Your game is ready"));
        await service.DisconnectAsync();
        Assert.Equal(SteamyNotificationKind.Download, Assert.Single(center.Items).Content.Kind);
    }
    private static DiscordChatMessage Message(ulong id) => new(id, 2, 2, 0, 4, "Let's play", DateTimeOffset.UtcNow, false, false, false, "Two");
    private sealed class Settings : ISettingsService
    {
        public AppSettings Value = new(); public AppSettings Load() => Value;
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Social : IDiscordSocialService, IDiscordChatService
    {
        public bool IsAvailable => true; public bool HasSavedSession => Snapshot.IsConnected; public string AvailabilityMessage => "Ready";
        public DiscordSnapshot Snapshot { get; private set; } = new(true, new(1, "one", "One", null), [new(new(2, "two", "Two", null), DiscordRelationshipKind.Friend, "Online")]);
        public bool HasCommunicationPermission => Snapshot.IsConnected; public DiscordParty? Party => null; public IReadOnlyList<DiscordPartyInvite> Invites => [];
        public event EventHandler? Changed; public event EventHandler<DiscordMessageChange>? MessageChanged;
        public IReadOnlyList<DiscordChatMessage> History = [];
        public void Emit(DiscordChatMessage message) => MessageChanged?.Invoke(this, new(message));
        public Task RestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token = default) { Snapshot = DiscordSnapshot.Disconnected; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task SendFriendRequestAsync(string value, CancellationToken token = default) => Task.CompletedTask;
        public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peer, bool party, CancellationToken token = default) => Task.FromResult(History);
        public Task<DiscordChatMessage> SendMessageAsync(ulong peer, bool party, string text, CancellationToken token = default) => Task.FromResult(Message(99));
        public void SetShowingChat(bool showing) { }
        public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default) => Task.CompletedTask;
        public Task InviteToPartyAsync(ulong id, CancellationToken token = default) => Task.CompletedTask;
        public Task AcceptPartyInviteAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
        public void DismissPartyInvite(Guid id) { }
        public Task LeavePartyAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
