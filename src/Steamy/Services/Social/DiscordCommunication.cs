using System.Globalization;

namespace Steamy.Services;

public enum DiscordDeliveryState { Sent, Sending, Failed }
public interface IDiscordMessageDelivery
{
    Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default);
}
public sealed class DiscordSendRejectedException(string message) : DiscordSocialException(message);

public sealed record DiscordChatMessage(ulong Id, ulong AuthorId, ulong PeerId, ulong LobbyId, ulong ChannelId,
    string Content, DateTimeOffset SentAt, bool IsOwn, bool HasAdditionalContent, bool IsEdited, string AuthorName = "", string AdditionalTitle = "", string AdditionalKind = "", int AdditionalCount = 0)
{
    public string ClientNonce { get; init; } = "";
    public DiscordDeliveryState DeliveryState { get; init; } = DiscordDeliveryState.Sent;
    public string DeliveryError { get; init; } = "";
    public bool CanResend { get; init; }
    public DiscordVoiceInvite? VoiceInvite { get; init; }
    public bool HasVoiceInvite => VoiceInvite is not null;
    public bool NeedsDeliveryAction => DeliveryState == DiscordDeliveryState.Failed;
    public string DeliveryAction => CanResend ? "Retry" : "Check delivery";
    public string DayLabel => SentAt.LocalDateTime.Date == DateTime.Today ? "Today" : SentAt.LocalDateTime.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : SentAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    public DateTime DayKey => SentAt.LocalDateTime.Date;
    public string? ImageUrl => ChatMediaPolicy.ImageUrl(Content);
    public string DisplayContent => Content.Trim() == ImageUrl ? "" : Content;
    public string MediaLabel => AdditionalTitle.Length > 0 ? AdditionalTitle : AdditionalCount > 1 ? $"{AdditionalCount} attachments" : AdditionalKind switch
    { "Poll" => "Discord poll", "VoiceMessage" => "Voice message", "Thread" => "Discord thread", "Embed" => "Shared content", "Sticker" => "Sticker", "Attachment" => "Attachment", _ => "Shared Discord content" };
    public string TimeLabel => (!IsOwn && AuthorName.Length > 0 ? AuthorName + " · " : "")
        + SentAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + (IsEdited ? " · edited" : "")
        + (IsOwn ? DeliveryState switch { DiscordDeliveryState.Sending => " · Sending…", DiscordDeliveryState.Failed => CanResend ? " · Failed" : " · Not confirmed", _ => " · Sent" } : "");
}
public interface IDiscordMessageNavigation
{
    Task OpenMessageAsync(ulong messageId, CancellationToken token = default);
}
public sealed record DiscordMessageChange(DiscordChatMessage? Message, ulong DeletedId = 0);
public sealed record DiscordPartyGame(int AppId, string Name);
public sealed record DiscordParty(ulong Id, DiscordPartyGame Game, IReadOnlyList<DiscordProfile> Members);
public sealed record DiscordPartyInvite(Guid Id, ulong SenderId, string SenderName, string Label);

public interface IDiscordChatService
{
    bool HasCommunicationPermission { get; }
    DiscordParty? Party { get; }
    IReadOnlyList<DiscordPartyInvite> Invites { get; }
    event EventHandler<DiscordMessageChange>? MessageChanged;
    Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default);
    Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default);
    void SetShowingChat(bool showing);
    Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default);
    Task InviteToPartyAsync(ulong friendId, CancellationToken token = default);
    Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default);
    void DismissPartyInvite(Guid inviteId);
    Task LeavePartyAsync(CancellationToken token = default);
}

internal interface IDiscordCommunicationClient
{
    DiscordParty? Party { get; }
    IReadOnlyList<DiscordPartyInvite> Invites { get; }
    event EventHandler<DiscordMessageChange>? MessageChanged;
    Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token);
    Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token);
    void SetShowingChat(bool showing);
    Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token);
    Task InviteToPartyAsync(ulong friendId, CancellationToken token);
    Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token);
    void DismissPartyInvite(Guid inviteId);
    Task LeavePartyAsync(CancellationToken token);
}

public sealed partial class DiscordSocialService : IDiscordChatService, IDiscordMessageNavigation, IDiscordMessageDelivery
{
    public Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(nonce, "N", out _) || content.Trim().Length is 0 or > 2000) throw new ArgumentException("Invalid outgoing message.");
        return CommunicationOperationAsync((client, ct) =>
        {
            ValidateConversation(peerId, party);
            return client is IDiscordMessageDelivery delivery ? delivery.SendWithNonceAsync(peerId, party, content.Trim(), nonce, ct)
                : client.SendMessageAsync(peerId, party, content.Trim(), ct);
        }, token);
    }
    public Task OpenMessageAsync(ulong messageId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        if (client is not IDiscordMessageNavigation navigation) throw new DiscordSocialException("This Discord SDK cannot open the message.");
        await navigation.OpenMessageAsync(messageId, ct); return true;
    }, token);
    public bool HasCommunicationPermission => Snapshot.IsConnected && _session?.Scopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains("sdk.social_layer", StringComparer.Ordinal) == true;
    public DiscordParty? Party => (_client as IDiscordCommunicationClient)?.Party;
    public IReadOnlyList<DiscordPartyInvite> Invites => (_client as IDiscordCommunicationClient)?.Invites ?? [];
    public event EventHandler<DiscordMessageChange>? MessageChanged;

    public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default) =>
        CommunicationOperationAsync((client, ct) => { ValidateConversation(peerId, party); return client.GetMessagesAsync(peerId, party, ct); }, token);
    public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default)
    {
        var text = content.Trim();
        if (text.Length is 0 or > 2000) throw new ArgumentException("Write a message of up to 2,000 characters.");
        return CommunicationOperationAsync((client, ct) => { ValidateConversation(peerId, party); return client.SendMessageAsync(peerId, party, text, ct); }, token);
    }
    public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default)
    {
        if (game.AppId <= 0 || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 200)
            throw new ArgumentException("Choose a game from your library.");
        return CommunicationOperationAsync(async (client, ct) => { await client.CreatePartyAsync(game, ct); return true; }, token);
    }
    public Task InviteToPartyAsync(ulong friendId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        ValidateConversation(friendId, false);
        if (Party is null) throw new InvalidOperationException("Create a party before inviting friends.");
        await client.InviteToPartyAsync(friendId, ct); return true;
    }, token);
    public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        if (!Invites.Any(invite => invite.Id == inviteId)) throw new ArgumentException("This invitation is no longer available.");
        await client.AcceptPartyInviteAsync(inviteId, ct); return true;
    }, token);
    public Task LeavePartyAsync(CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    { await client.LeavePartyAsync(ct); return true; }, token);
    public void DismissPartyInvite(Guid inviteId) { (_client as IDiscordCommunicationClient)?.DismissPartyInvite(inviteId); Changed?.Invoke(this, EventArgs.Empty); }
    public void SetShowingChat(bool showing) { if (_client is IDiscordCommunicationClient client) client.SetShowingChat(showing && HasCommunicationPermission); }

    private void ValidateConversation(ulong peerId, bool party)
    {
        if (party ? Party?.Id != peerId : !Snapshot.Relationships.Any(friend => friend.Id == peerId && friend.IsFriend))
            throw new InvalidOperationException(party ? "This party has ended. Join or create another party." : "Choose a current Discord friend to chat with.");
    }
    private Task<T> CommunicationOperationAsync<T>(Func<IDiscordCommunicationClient, CancellationToken, Task<T>> action, CancellationToken token) =>
        ClientOperationAsync((client, ct) => client is IDiscordCommunicationClient communication ? action(communication, ct)
            : throw new InvalidOperationException("Reconnect Discord to enable chat and parties."), true, token);
    private void ClientMessageChanged(object? sender, DiscordMessageChange change)
    {
        if (ReferenceEquals(sender, _client) && HasCommunicationPermission) MessageChanged?.Invoke(this, change);
    }
}
