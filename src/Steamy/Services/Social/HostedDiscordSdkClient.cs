namespace Steamy.Services;

/// <summary>Only immutable managed snapshots cross to WPF. All native objects stay on the SDK host.</summary>
internal sealed class HostedDiscordSdkClient : IDiscordSocialClient, IDiscordCommunicationClient, IDiscordMessageNavigation, IDiscordMessageDelivery, IDiscordPartyCodes, IDiscordVoiceService
{
    private readonly IDiscordSdkHost _host;
    private readonly Task<DiscordSdkClient> _native;
    private readonly CancellationTokenSource _lifetime = new();
    private DiscordSnapshot _snapshot = DiscordSnapshot.Disconnected;
    private DiscordParty? _party;
    private string? _partyCode;
    private DiscordVoiceSnapshot _voice = DiscordVoiceSnapshot.Empty;
    public DiscordVoiceSnapshot Voice => Volatile.Read(ref _voice);
    public event EventHandler? VoiceChanged;
    public string? PartyCode => Volatile.Read(ref _partyCode);
    private IReadOnlyList<DiscordPartyInvite> _invites = [];
    private int _disposed;
    public DiscordSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public DiscordParty? Party => Volatile.Read(ref _party);
    public IReadOnlyList<DiscordPartyInvite> Invites => Volatile.Read(ref _invites);
    public event EventHandler? Changed;
    public event EventHandler? TokenExpired;
    public event EventHandler<DiscordMessageChange>? MessageChanged;
    public HostedDiscordSdkClient(string directory, IDiscordSdkHost? host = null)
    {
        _host = host ?? DiscordSdkHost.Shared;
        _native = _host.InvokeAsync(() =>
        {
            var client = new DiscordSdkClient(directory);
            client.Changed += (_, _) =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                Volatile.Write(ref _snapshot, client.Snapshot); Volatile.Write(ref _party, client.Party); Volatile.Write(ref _partyCode, client.PartyCode); Volatile.Write(ref _invites, client.Invites.ToArray());
                Changed?.Invoke(this, EventArgs.Empty);
            };
            client.TokenExpired += (_, _) => { if (Volatile.Read(ref _disposed) == 0) TokenExpired?.Invoke(this, EventArgs.Empty); };
            client.VoiceChanged += (_, _) => { if (Volatile.Read(ref _disposed) == 0) { Volatile.Write(ref _voice, client.Voice); VoiceChanged?.Invoke(this, EventArgs.Empty); } };
            client.MessageChanged += (_, change) => { if (Volatile.Read(ref _disposed) == 0) MessageChanged?.Invoke(this, change); };
            return client;
        });
    }
    private async Task<T> CallAsync<T>(Func<DiscordSdkClient, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var client = await _native.WaitAsync(linked.Token).ConfigureAwait(false);
        var pending = await _host.InvokeAsync(() =>
        { linked.Token.ThrowIfCancellationRequested(); return operation(client, linked.Token); }, linked.Token).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }
    private Task CallAsync(Func<DiscordSdkClient, CancellationToken, Task> operation, CancellationToken token) => CallAsync(async (client, ct) => { await operation(client, ct); return true; }, token);
    public Task<DiscordSession> AuthorizeAsync(ulong id, CancellationToken token) => CallAsync((client, ct) => client.AuthorizeAsync(id, ct), token);
    public Task<DiscordSession> RefreshAsync(DiscordSession session, CancellationToken token) => CallAsync((client, ct) => client.RefreshAsync(session, ct), token);
    public Task ConnectAsync(ulong id, string accessToken, CancellationToken token) => CallAsync((client, ct) => client.ConnectAsync(id, accessToken, ct), token);
    public Task RevokeAsync(DiscordSession session, CancellationToken token) => CallAsync((client, ct) => client.RevokeAsync(session, ct), token);
    public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token) => CallAsync((client, ct) => client.SendFriendRequestAsync(usernameOrId, ct), token);
    public Task UpdateFriendAsync(ulong id, DiscordFriendAction action, CancellationToken token) => CallAsync((client, ct) => client.UpdateFriendAsync(id, action, ct), token);
    public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token) => CallAsync((client, ct) => client.GetMessagesAsync(peerId, party, ct), token);
    public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token) => CallAsync((client, ct) => client.SendMessageAsync(peerId, party, content, ct), token);
    public Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default) => CallAsync((client, ct) => client.SendWithNonceAsync(peerId, party, content, nonce, ct), token);
    public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token) => CallAsync((client, ct) => client.CreatePartyAsync(game, ct), token);
    public Task JoinPartyCodeAsync(string code, CancellationToken token = default) => CallAsync((client, ct) => client.JoinPartyCodeAsync(code, ct), token);
    public Task JoinPartyVoiceAsync(CancellationToken token = default) => CallAsync((client, ct) => client.JoinPartyVoiceAsync(ct), token);
    public Task StartPrivateCallAsync(ulong friendId, CancellationToken token = default) => CallAsync((client, ct) => client.StartPrivateCallAsync(friendId, ct), token);
    public Task AcceptVoiceInviteAsync(DiscordVoiceInvite invite, CancellationToken token = default) => CallAsync((client, ct) => client.AcceptVoiceInviteAsync(invite, ct), token);
    public Task LeaveVoiceAsync(CancellationToken token = default) => CallAsync((client, ct) => client.LeaveVoiceAsync(ct), token);
    public Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default) => CallAsync((client, ct) => client.GetAudioDevicesAsync(ct), token);
    public Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default) => CallAsync((client, ct) => client.SetVoiceOptionsAsync(options, ct), token);
    public Task SetParticipantVolumeAsync(ulong userId, float volume, CancellationToken token = default) => CallAsync((client, ct) => client.SetParticipantVolumeAsync(userId, volume, ct), token);
    public Task InviteToPartyAsync(ulong id, CancellationToken token) => CallAsync((client, ct) => client.InviteToPartyAsync(id, ct), token);
    public Task AcceptPartyInviteAsync(Guid id, CancellationToken token) => CallAsync((client, ct) => client.AcceptPartyInviteAsync(id, ct), token);
    public Task LeavePartyAsync(CancellationToken token) => CallAsync((client, ct) => client.LeavePartyAsync(ct), token);
    public Task OpenMessageAsync(ulong id, CancellationToken token = default) => CallAsync((client, ct) => client.OpenMessageAsync(id, ct), token);
    public void DismissPartyInvite(Guid id) => Queue(client => client.DismissPartyInvite(id));
    public void SetShowingChat(bool showing) => Queue(client => client.SetShowingChat(showing));
    private async void Queue(Action<DiscordSdkClient> operation)
    {
        try
        {
            var client = await _native.ConfigureAwait(false);
            await _host.InvokeAsync(() => { if (Volatile.Read(ref _disposed) == 0) operation(client); return true; }).ConfigureAwait(false);
        }
        catch (Exception) { /* Advisory visibility/state changes must not crash WPF. Required operations return errors. */ }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); Volatile.Write(ref _snapshot, DiscordSnapshot.Disconnected); Volatile.Write(ref _party, null); Volatile.Write(ref _partyCode, null); Volatile.Write(ref _invites, []);
        Volatile.Write(ref _voice, DiscordVoiceSnapshot.Empty);
        _ = DisposeNativeAsync();
    }
    private async Task DisposeNativeAsync()
    {
        try { var client = await _native.ConfigureAwait(false); await _host.InvokeAsync(() => { client.Dispose(); return true; }).ConfigureAwait(false); }
        catch (Exception) { }
        finally { _lifetime.Dispose(); }
    }
}
