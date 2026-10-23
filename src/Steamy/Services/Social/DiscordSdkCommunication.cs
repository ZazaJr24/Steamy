using System.Collections;
using System.Globalization;
using System.Security.Cryptography;

namespace Steamy.Services;

internal sealed partial class DiscordSdkClient : IDiscordMessageNavigation, IDiscordMessageDelivery, IDiscordPartyCodes
{
    public Task OpenMessageAsync(ulong messageId, CancellationToken token = default)
    {
        if (!(bool)Invoke(_client, "CanOpenMessageInDiscord", messageId)!) throw new DiscordSocialException("This message isn't available in Discord. Ephemeral party messages stay in Steamy.");
        return ResultAsync("OpenMessageInDiscord", [messageId], token);
    }
    private readonly Dictionary<Guid, (DiscordPartyInvite Info, object Native)> _invites = [];
    private string? _partySecret;
    private readonly SemaphoreSlim _partyMutation = new(1);
    public string? PartyCode => Party is not null && _partySecret is { Length: 64 } secret && secret.All(char.IsAsciiHexDigit) ? DiscordPartyCode.Encode(secret) : null;
    private async Task PartyMutationAsync(Func<Task> operation, CancellationToken token)
    { await _partyMutation.WaitAsync(token); try { await operation(); } finally { _partyMutation.Release(); } }
    public DiscordParty? Party { get; private set; }
    public IReadOnlyList<DiscordPartyInvite> Invites => _invites.Values.Select(value => value.Info).ToArray();
    public event EventHandler<DiscordMessageChange>? MessageChanged;

    private void InitializeCommunicationCallbacks()
    {
        SetCallback("SetMessageCreatedCallback", args => NotifyMessage(Convert.ToUInt64(args[0])));
        SetCallback("SetMessageUpdatedCallback", args => NotifyMessage(Convert.ToUInt64(args[0])));
        SetCallback("SetMessageDeletedCallback", args =>
        {
            var messageId = Convert.ToUInt64(args[0]);
            foreach (var key in _invites.Where(pair => pair.Value.Native is not string && Convert.ToUInt64(Call(pair.Value.Native, "MessageId")) == messageId).Select(pair => pair.Key).ToArray()) DismissPartyInvite(key);
            MessageChanged?.Invoke(this, new(null, messageId));
        });
        SetCallback("SetActivityInviteCreatedCallback", args => ReceiveInvite(args[0]!));
        SetCallback("SetActivityInviteUpdatedCallback", args => ReceiveInvite(args[0]!));
        SetCallback("SetLobbyMemberAddedCallback", _ => { RefreshParty(); RefreshPrivateVoiceMembership(); });
        SetCallback("SetLobbyMemberRemovedCallback", _ => { RefreshParty(); RefreshPrivateVoiceMembership(); });
        SetCallback("SetLobbyUpdatedCallback", _ => { RefreshParty(); RefreshPrivateVoiceMembership(); });
        SetCallback("SetLobbyDeletedCallback", args =>
        {
            var id = Convert.ToUInt64(args[0]);
            if (Voice.LobbyId == id) _ = EndFailedVoiceAsync("The voice lobby closed.");
            if (Party?.Id == id) { Party = null; _partySecret = null; Invoke(_client, "ClearRichPresence"); Changed?.Invoke(this, EventArgs.Empty); }
        });
        // A Join action made in Discord still appears as a reviewable invitation in Steamy.
        // It never launches a local executable or reads an executable path from a remote party.
        SetCallback("SetActivityJoinCallback", args =>
        {
            var secret = args[0] as string;
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 1024) return;
            if (_invites.Values.Any(value => value.Native is string existing && existing == secret)) return;
            var id = Guid.NewGuid();
            _invites[id] = (new(id, 0, "Discord", "Party invitation from Discord"), secret);
            TrimInvites(); Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void NotifyMessage(ulong id)
    {
        var message = Invoke(_client, "GetMessageHandle", id);
        if (message is null) return;
        try { MessageChanged?.Invoke(this, new(ReadMessage(message))); }
        finally { DisposeObject(message); }
    }

    public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token) =>
        RequestAsync(party ? "GetLobbyMessagesWithLimit" : "GetUserMessagesWithLimit", [peerId, 200], values =>
        {
            CheckResult(values[0]);
            var result = new List<DiscordChatMessage>();
            foreach (var message in (IEnumerable)values[1]!)
            {
                try { result.Add(ReadMessage(message)); }
                finally { DisposeObject(message); }
            }
            return (IReadOnlyList<DiscordChatMessage>)result.OrderBy(message => message.SentAt).ThenBy(message => message.Id).ToArray();
        }, token);

    public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token) => SendWithNonceAsync(peerId, party, content, Guid.NewGuid().ToString("N"), token);
    public async Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default)
    {
        var id = await RequestAsync(party ? "SendLobbyMessageWithMetadata" : "SendUserMessageWithMetadata", [peerId, content, new Dictionary<string, string> { ["steamy.client_nonce"] = nonce }], values =>
        { CheckResult(values[0]); return Convert.ToUInt64(values[1]); }, token);
        var native = Invoke(_client, "GetMessageHandle", id);
        if (native is null)
            return new DiscordChatMessage(id, Snapshot.Profile?.Id ?? 0, party ? 0 : peerId, party ? peerId : 0, 0, content, DateTimeOffset.UtcNow, true, false, false) { ClientNonce = nonce };
        try { return ReadMessage(native) with { ClientNonce = nonce }; }
        finally { DisposeObject(native); }
    }

    private DiscordChatMessage ReadMessage(object native)
    {
        var ownId = Snapshot.Profile?.Id ?? 0;
        var authorId = Convert.ToUInt64(Call(native, "AuthorId"));
        var recipientId = Convert.ToUInt64(Call(native, "RecipientId"));
        var lobby = Call(native, "Lobby");
        ulong lobbyId;
        try { lobbyId = lobby is null ? 0 : Convert.ToUInt64(Call(lobby, "Id")); }
        finally { DisposeObject(lobby); }
        var extra = Call(native, "AdditionalContent");
        bool hasExtra; string title = "", kind = ""; int count = 0;
        try
        {
            hasExtra = extra is not null;
            if (extra is not null)
            {
                title = ((Call(extra, "Title") as string) ?? "");
                if (title.Length > 160) title = title[..160];
                kind = Call(extra, "Type")?.ToString() ?? "";
                count = Math.Clamp(Convert.ToInt32(Call(extra, "Count")), 0, 100);
            }
        }
        finally { DisposeObject(extra); }
        var milliseconds = Convert.ToUInt64(Call(native, "SentTimestamp"));
        var time = milliseconds <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) : DateTimeOffset.UtcNow;
        return new(Convert.ToUInt64(Call(native, "Id")), authorId, authorId == ownId ? recipientId : authorId,
            lobbyId, Convert.ToUInt64(Call(native, "ChannelId")), Call(native, "Content") as string ?? "", time, authorId == ownId,
            hasExtra, Convert.ToUInt64(Call(native, "EditedTimestamp")) > 0,
            Snapshot.Relationships.FirstOrDefault(friend => friend.Id == authorId)?.DisplayName
                ?? Party?.Members.FirstOrDefault(member => member.Id == authorId)?.DisplayName ?? "", title, kind, count)
        { ClientNonce = authorId == ownId && Call(native, "Metadata") is IDictionary metadata && metadata["steamy.client_nonce"] is string nonce && Guid.TryParseExact(nonce, "N", out _) ? nonce : "",
          VoiceInvite = lobbyId == 0 ? DiscordVoiceProtocol.Read(Call(native, "Metadata") as IDictionary, _applicationId, authorId) : null };
    }
    public void SetShowingChat(bool showing) { if (!_disposed) Invoke(_client, "SetShowingChat", showing); }

    public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token) => PartyMutationAsync(() => CreatePartyCoreAsync(game, token), token);
    private async Task CreatePartyCoreAsync(DiscordPartyGame game, CancellationToken token)
    {
        if (Party is not null) throw new InvalidOperationException("Leave your current party before creating another.");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var metadata = DiscordLobbyProtocol.PartyMetadata(_applicationId, Snapshot.Profile?.Id ?? 0, game);
        var id = await RequestAsync("CreateOrJoinLobbyWithMetadata", [secret, metadata, new Dictionary<string, string>()], values =>
        { CheckResult(values[0]); return Convert.ToUInt64(values[1]); }, token);
        Party = new(id, game, []); _partySecret = secret;
        try { RefreshParty(); await PublishPartyAsync(token); }
        catch
        {
            try { await ResultAsync("LeaveLobby", [id], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            Party = null; _partySecret = null; Changed?.Invoke(this, EventArgs.Empty); throw;
        }
    }

    public async Task InviteToPartyAsync(ulong friendId, CancellationToken token)
    {
        RefreshParty(); await PublishPartyAsync(token);
        await ResultAsync("SendActivityInvite", [friendId, "Join my Steamy party for " + (Party?.Game.Name ?? "our next game") + ". Open Steamy on your PC, connect Discord and accept under Invitations."], token);
    }

    private void ReceiveInvite(object invite)
    {
        var senderId = Convert.ToUInt64(Call(invite, "SenderId"));
        if (senderId == Snapshot.Profile?.Id) { DisposeObject(invite); return; }
        if (Convert.ToUInt64(Call(invite, "ApplicationId")) != _applicationId) { DisposeObject(invite); return; }
        // Requests to join need a different reply flow; only explicit incoming invites belong here.
        if (Call(invite, "Type")?.ToString() != "Join") { DisposeObject(invite); return; }
        var messageId = Convert.ToUInt64(Call(invite, "MessageId"));
        var existing = _invites.FirstOrDefault(pair => pair.Value.Native is not string && Convert.ToUInt64(Call(pair.Value.Native, "MessageId")) == messageId);
        var id = existing.Value.Native is not null ? existing.Key : Guid.NewGuid();
        if (existing.Value.Native is not null) DisposeObject(existing.Value.Native);
        var sender = Snapshot.Relationships.FirstOrDefault(friend => friend.Id == senderId)?.DisplayName ?? "Discord friend";
        _invites[id] = (new(id, senderId, sender, "Invited you to play together"), invite);
        TrimInvites(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void TrimInvites()
    {
        while (_invites.Count > 32) DismissPartyInvite(_invites.Keys.First());
    }
    public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token) => PartyMutationAsync(() => AcceptPartyInviteCoreAsync(inviteId, token), token);
    private async Task AcceptPartyInviteCoreAsync(Guid inviteId, CancellationToken token)
    {
        if (!_invites.TryGetValue(inviteId, out var invite)) throw new ArgumentException("This invitation has expired.");
        if (Party is not null) throw new InvalidOperationException("Leave your current party before joining another.");
        var secret = invite.Native is string readySecret ? readySecret : await RequestAsync("AcceptActivityInvite", [invite.Native], values =>
        { CheckResult(values[0]); return (string)values[1]!; }, token);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 1024) throw new DiscordSocialException("This invitation is invalid or expired.");
        await JoinPartySecretAsync(secret, true, token);
        DismissPartyInvite(inviteId);
    }
    public Task JoinPartyCodeAsync(string code, CancellationToken token = default) => PartyMutationAsync(async () =>
    {
        if (Party is not null) throw new InvalidOperationException("Leave your current party first.");
        if (!DiscordPartyCode.TryDecode(code, out var secret)) throw new ArgumentException("Paste the complete STMY1 party code.");
        await JoinPartySecretAsync(secret, false, token);
    }, token);
    private async Task JoinPartySecretAsync(string secret, bool allowLegacy, CancellationToken token)
    {
        var id = await RequestAsync("CreateOrJoinLobby", [secret], values => Convert.ToUInt64(values[1]), token);
        try
        {
            var lobby = Invoke(_client, "GetLobbyHandle", id);
            if (lobby is null) throw new DiscordSocialException("This party is no longer available.");
            DiscordPartyGame game;
            try { game = DiscordLobbyProtocol.ReadParty((IDictionary)Call(lobby, "Metadata")!, _applicationId, allowLegacy); }
            finally { DisposeObject(lobby); }
            token.ThrowIfCancellationRequested();
            _partySecret = secret; Party = new(id, game, []); RefreshParty(); await PublishPartyAsync(token);
        }
        catch
        {
            // CreateOrJoinLobby creates a new empty lobby for an unknown/expired secret.
            // Leave that membership and never present it as a successful party.
            try { await ResultAsync("LeaveLobby", [id], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            Party = null; _partySecret = null; Changed?.Invoke(this, EventArgs.Empty); throw;
        }
    }
    public void DismissPartyInvite(Guid inviteId)
    {
        if (_invites.Remove(inviteId, out var invite)) DisposeObject(invite.Native);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task LeavePartyAsync(CancellationToken token) => PartyMutationAsync(() => LeavePartyCoreAsync(token), token);
    private async Task LeavePartyCoreAsync(CancellationToken token)
    {
        if (Party is not { } party) return;
        if (Voice.LobbyId == party.Id) await LeaveVoiceAsync(token);
        await ResultAsync("LeaveLobby", [party.Id], token);
        Party = null; _partySecret = null;
        Invoke(_client, "ClearRichPresence"); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshParty()
    {
        if (Party is not { } party || _disposed) return;
        var lobby = Invoke(_client, "GetLobbyHandle", party.Id);
        if (lobby is null) return;
        try
        {
            DiscordPartyGame game;
            try { game = DiscordLobbyProtocol.ReadParty(Call(lobby, "Metadata") as IDictionary, _applicationId, allowLegacy: true); }
            catch (DiscordSocialException)
            {
                if (Voice.LobbyId == party.Id) _ = EndFailedVoiceAsync("The party is no longer available.");
                Party = null; _partySecret = null; Invoke(_client, "ClearRichPresence");
                _ = LeaveUnusedVoiceLobbyAsync(party.Id); Changed?.Invoke(this, EventArgs.Empty); return;
            }
            var members = new List<DiscordProfile>();
            foreach (var id in (IEnumerable)Call(lobby, "LobbyMemberIds")!)
            {
                var user = Invoke(_client, "GetUser", Convert.ToUInt64(id));
                try { if (Profile(user) is { } profile) members.Add(profile); }
                finally { DisposeObject(user); }
                if (members.Count >= 128) break;
            }
            Party = new(party.Id, game, members); Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { DisposeObject(lobby); }
    }
    private async Task PublishPartyAsync(CancellationToken token)
    {
        if (Party is not { } party || _partySecret is null) return;
        var activity = New("Activity"); var nativeParty = New("ActivityParty"); var secrets = New("ActivitySecrets"); var assets = New("ActivityAssets");
        try
        {
            Invoke(activity, "SetType", Enum.Parse(Type("ActivityTypes"), "Playing"));
            Invoke(activity, "SetState", "Playing together in Steamy");
            Invoke(activity, "SetDetails", party.Game.Name);
            if (party.Game.AppId > 0)
            {
                Invoke(assets, "SetInviteCoverImage", $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{party.Game.AppId}/header.jpg");
                Invoke(activity, "SetAssets", assets);
            }
            Invoke(activity, "SetSupportedPlatforms", Enum.Parse(Type("ActivityGamePlatforms"), "Desktop"));
            Invoke(nativeParty, "SetId", party.Id.ToString(CultureInfo.InvariantCulture));
            Invoke(nativeParty, "SetCurrentSize", Math.Max(1, party.Members.Count));
            Invoke(nativeParty, "SetMaxSize", Math.Max(8, party.Members.Count));
            Invoke(activity, "SetParty", nativeParty);
            Invoke(secrets, "SetJoin", _partySecret); Invoke(activity, "SetSecrets", secrets);
            await ResultAsync("UpdateRichPresence", [activity], token);
        }
        finally { DisposeObject(assets); DisposeObject(secrets); DisposeObject(nativeParty); DisposeObject(activity); }
    }
    private void DisposeCommunication()
    {
        foreach (var value in _invites.Values) DisposeObject(value.Native);
        _invites.Clear(); Party = null; _partySecret = null;
    }
}
