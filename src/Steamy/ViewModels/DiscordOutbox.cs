using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class DiscordFriendsViewModel
{
    private sealed record Outgoing(string Key, ulong PeerId, bool Party, DiscordChatMessage Message);
    private readonly Dictionary<string, Outgoing> _outbox = [];
    private readonly HashSet<string> _sendingConversations = [];
    private CancellationTokenSource _outboxLifetime = new();
    private long _localMessageId;
    public IAsyncRelayCommand<DiscordChatMessage> RetryMessageCommand { get; private set; } = null!;
    public IRelayCommand<DiscordChatMessage> DismissLocalMessageCommand { get; private set; } = null!;
    private static string MessageConversation(DiscordChatMessage message) => message.LobbyId != 0 ? "party:" + message.LobbyId : "friend:" + message.PeerId;
    private async Task SendMessageAsync()
    {
        if (_chat is null || ConversationId == 0 || _sendingConversations.Contains(ConversationKey) || MessageDraft.Trim().Length is 0 or > 2000) return;
        var nonce = Guid.NewGuid().ToString("N"); var id = ConversationId; var party = _partyChat;
        var message = new DiscordChatMessage(ulong.MaxValue - (ulong)Interlocked.Increment(ref _localMessageId), _discord.Snapshot.Profile?.Id ?? 0, party ? 0 : id, party ? id : 0, 0,
            MessageDraft.Trim(), DateTimeOffset.UtcNow, true, false, false) { ClientNonce = nonce, DeliveryState = DiscordDeliveryState.Sending };
        var outgoing = new Outgoing(ConversationKey, id, party, message);
        _outbox[nonce] = outgoing; MessageDraft = ""; _conversationDrafts.Remove(outgoing.Key); UpsertMessage(message);
        await DeliverAsync(outgoing);
    }
    private async Task DeliverAsync(Outgoing outgoing)
    {
        var nonce = outgoing.Message.ClientNonce;
        if (!_sendingConversations.Add(outgoing.Key)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_outboxLifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        RefreshWorkspaceCommands();
        try
        {
            var sent = _chat is IDiscordMessageDelivery delivery
                ? await delivery.SendWithNonceAsync(outgoing.PeerId, outgoing.Party, outgoing.Message.Content, nonce, deadline.Token)
                : await _chat!.SendMessageAsync(outgoing.PeerId, outgoing.Party, outgoing.Message.Content, deadline.Token);
            if (!_outbox.ContainsKey(nonce)) return; // A live echo or logout already resolved it.
            var confirmed = sent with { ClientNonce = nonce, DeliveryState = DiscordDeliveryState.Sent };
            if (ConversationKey == outgoing.Key) { UpsertMessage(confirmed); ChatStatus = ""; }
            else _outbox[nonce] = outgoing with { Message = confirmed }; // Merge on the next history load.
        }
        catch (Exception error)
        {
            if (!_outbox.ContainsKey(nonce) || deadline.IsCancellationRequested && _outboxLifetime.IsCancellationRequested) return;
            if (_outbox[nonce].Message.DeliveryState == DiscordDeliveryState.Sent) return;
            var rejected = error is DiscordSendRejectedException or ArgumentException or InvalidOperationException;
            var detail = error is DiscordSocialException or ArgumentException or InvalidOperationException ? error.Message : "Delivery wasn't confirmed. Check delivery before sending it again.";
            var failed = outgoing.Message with { DeliveryState = DiscordDeliveryState.Failed, DeliveryError = detail, CanResend = rejected };
            _outbox[nonce] = outgoing with { Message = failed };
            if (ConversationKey == outgoing.Key) { UpsertMessage(failed); ChatStatus = detail; }
        }
        finally { _sendingConversations.Remove(outgoing.Key); RefreshWorkspaceCommands(); }
    }
    private async Task RetryMessageAsync(DiscordChatMessage? message)
    {
        if (message is null || _chat is null || !_outbox.TryGetValue(message.ClientNonce, out var outgoing) || _sendingConversations.Contains(outgoing.Key)) return;
        _sendingConversations.Add(outgoing.Key); RefreshWorkspaceCommands();
        try
        {
            var history = await _chat.GetMessagesAsync(outgoing.PeerId, outgoing.Party, _outboxLifetime.Token);
            var confirmed = history.FirstOrDefault(item => item.IsOwn && item.ClientNonce == message.ClientNonce);
            if (confirmed is not null)
            {
                if (ConversationKey == outgoing.Key) UpsertMessage(confirmed); else _outbox[message.ClientNonce] = outgoing with { Message = confirmed };
                return;
            }
            if (!message.CanResend)
            {
                ChatStatus = "Delivery is still unconfirmed. Check this conversation in Discord before composing it again.";
                return; // A history window is not proof that a timed-out send was rejected.
            }
        }
        catch (Exception error) { ChatStatus = error is DiscordSocialException ? error.Message : "Couldn't check delivery. Try again after reconnecting."; return; }
        finally { _sendingConversations.Remove(outgoing.Key); RefreshWorkspaceCommands(); }
        if (!_outbox.ContainsKey(message.ClientNonce)) return;
        outgoing = outgoing with { Message = outgoing.Message with { DeliveryState = DiscordDeliveryState.Sending, DeliveryError = "", CanResend = false } };
        _outbox[message.ClientNonce] = outgoing; if (ConversationKey == outgoing.Key) UpsertMessage(outgoing.Message);
        await DeliverAsync(outgoing);
    }
    private void ReconcileOutgoing(DiscordChatMessage message)
    {
        if (!message.IsOwn || message.DeliveryState != DiscordDeliveryState.Sent || message.ClientNonce.Length == 0 || !_outbox.TryGetValue(message.ClientNonce, out var outgoing) || MessageConversation(message) != outgoing.Key) return;
        if (ConversationKey != outgoing.Key) { _outbox[message.ClientNonce] = outgoing with { Message = message }; return; }
        _outbox.Remove(message.ClientNonce);
        var placeholder = Messages.FirstOrDefault(item => item.Id == outgoing.Message.Id && item.Id != message.Id);
        if (placeholder is not null) Messages.Remove(placeholder);
    }
    private void ResetOutbox()
    {
        if (_outbox.Count == 0 && _sendingConversations.Count == 0) return;
        _outboxLifetime.Cancel(); _outboxLifetime.Dispose(); _outboxLifetime = new(); _outbox.Clear(); _sendingConversations.Clear();
    }
}
