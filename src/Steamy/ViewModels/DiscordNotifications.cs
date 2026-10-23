using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class DiscordFriendsViewModel
{
    private NotificationCenterViewModel? _notifications;
    private bool _windowActive;
    private bool _notificationsPrimed;
    private readonly HashSet<Guid> _knownInvites = [];
    private readonly HashSet<ulong> _knownRequests = [];
    private int _notificationEpoch;
    public int NotificationEpoch => _notificationEpoch;
    private void InitializeNotifications(NotificationCenterViewModel? notifications)
    { _notifications = notifications; if (_notifications is not null) _notifications.Changed += NotificationsChanged; }
    private void NotificationsChanged(object? sender, EventArgs args) { _notificationEpoch++; OnPropertyChanged(nameof(NotificationEpoch)); }
    public int UnreadMessages(ulong peerId) => _notifications?.ConversationUnreadCount(peerId) ?? 0;
    public void OpenFriendRequests()
    { SaveDraft(); _selectedFriend = null; _partyChat = false; ClearConversation(); OnPropertyChanged(nameof(SelectedFriend)); SelectedFilter = "Requests"; SearchText = ""; RefreshWorkspace(); }
    public void SetWindowActive(bool active) { _windowActive = active; _chat?.SetShowingChat(active && _chatVisible && ConversationVisibility == System.Windows.Visibility.Visible); MarkCurrentConversationRead(); }
    private void MarkCurrentConversationRead()
    { if (_windowActive && _chatVisible && ConversationId != 0) _notifications?.MarkConversationRead(_partyChat ? 0 : ConversationId, _partyChat ? ConversationId : 0); }
    private void NotifyIncomingMessage(DiscordChatMessage message)
    {
        if (_notifications is null || message.IsOwn || message.IsEdited) return;
        var current = message.LobbyId > 0 ? _partyChat && message.LobbyId == ConversationId : !_partyChat && message.PeerId == ConversationId;
        if (current && _chatVisible && _windowActive) { MarkCurrentConversationRead(); return; }
        var author = message.AuthorName.Length > 0 ? message.AuthorName : _discord.Snapshot.Relationships.FirstOrDefault(friend => friend.Id == message.AuthorId)?.DisplayName ?? "New message";
        var body = message.Content.Length == 0 ? message.MediaLabel : message.ImageUrl is not null && message.DisplayContent.Length == 0 ? "Sent an image" : message.Content;
        _notifications.Publish(new("social:message:" + message.Id, SteamyNotificationKind.Message, message.LobbyId > 0 ? "Party chat · " + author : author,
            body, message.PeerId, message.LobbyId, AvatarUrl: _discord.Snapshot.Relationships.FirstOrDefault(friend => friend.Id == message.AuthorId)?.AvatarUrl));
    }
    private void RefreshSocialNotifications()
    {
        if (_notifications is null) return;
        if (!IsConnected)
        { if (!_discord.HasSavedSession) { _notifications.ClearSocial(); _knownInvites.Clear(); _knownRequests.Clear(); _notificationsPrimed = false; } return; }
        var requests = _discord.Snapshot.Relationships.Where(friend => friend.IsIncoming).ToArray();
        foreach (var id in _knownRequests.Where(id => !requests.Any(friend => friend.Id == id))) _notifications.ResolveSocialTarget(SteamyNotificationKind.FriendRequest, id);
        foreach (var id in _knownInvites.Where(id => !PartyInvites.Any(invite => invite.Id == id))) _notifications.ResolveSocialTarget(SteamyNotificationKind.PartyInvite, inviteId: id);
        foreach (var request in requests.Where(friend => !_knownRequests.Contains(friend.Id)))
            _notifications.Publish(new("social:request:" + request.Id + ":" + Guid.NewGuid(), SteamyNotificationKind.FriendRequest, request.DisplayName,
                "Sent you a friend request", request.Id, AvatarUrl: request.AvatarUrl), _notificationsPrimed);
        foreach (var invite in PartyInvites.Where(invite => !_knownInvites.Contains(invite.Id)))
            _notifications.Publish(new("social:invite:" + invite.Id, SteamyNotificationKind.PartyInvite, invite.SenderName, invite.Label, invite.SenderId, InviteId: invite.Id), _notificationsPrimed);
        _knownRequests.Clear(); foreach (var request in requests) _knownRequests.Add(request.Id);
        _knownInvites.Clear(); foreach (var invite in PartyInvites) _knownInvites.Add(invite.Id);
        _notificationsPrimed = true;
    }
    private void DisposeNotifications() { if (_notifications is not null) _notifications.Changed -= NotificationsChanged; }
}
