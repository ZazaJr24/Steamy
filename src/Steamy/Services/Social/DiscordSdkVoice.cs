using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace Steamy.Services;

internal sealed partial class DiscordSdkClient : IDiscordVoiceService
{
    private object? _call;
    private DiscordVoiceOptions _voiceOptions = new();
    private DiscordVoiceOptions _requestedVoiceOptions = new();
    private readonly SemaphoreSlim _voiceDeviceUpdate = new(1);
    private DiscordVoiceInvite? _privateVoice;
    private readonly SemaphoreSlim _voiceMutation = new(1);
    private readonly List<Delegate> _voiceCallbacks = [];
    private readonly HashSet<ulong> _speaking = [];
    private DispatcherTimer? _pushToTalk;
    private bool _pttActive, _voiceConnected, _privateHadPeer;
    private long _voiceGeneration;
    public DiscordVoiceSnapshot Voice { get; private set; } = DiscordVoiceSnapshot.Empty;
    public event EventHandler? VoiceChanged;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private void InitializeVoiceCallbacks()
    {
        SetCallback("SetDeviceChangeCallback", values =>
        { foreach (var list in values.OfType<IEnumerable>()) foreach (var device in list) DisposeObject(device); VoiceChanged?.Invoke(this, EventArgs.Empty); });
    }
    private async Task VoiceMutationAsync(Func<Task> operation, CancellationToken token)
    { await _voiceMutation.WaitAsync(token); try { await operation(); } finally { _voiceMutation.Release(); } }
    public Task JoinPartyVoiceAsync(CancellationToken token = default) => VoiceMutationAsync(async () =>
    {
        if (Party is not { } party) throw new DiscordSocialException("Join a game party before entering party voice.");
        if (party.Members.Count > 25) throw new DiscordSocialException("Party voice supports up to 25 people. Start a smaller party to join voice.");
        if (Voice.LobbyId == party.Id) return;
        await LeaveVoiceCoreAsync(token); await StartVoiceAsync(party.Id, party.Game.Name + " · Party voice", null, token);
    }, token);
    public Task StartPrivateCallAsync(ulong friendId, CancellationToken token = default) => VoiceMutationAsync(async () =>
    {
        if (Snapshot.Profile is not { } me || friendId == me.Id || !Snapshot.Relationships.Any(friend => friend.Id == friendId && friend.IsFriend)) throw new DiscordSocialException("Choose another Discord friend to call.");
        await LeaveVoiceCoreAsync(token);
        var invitation = new DiscordVoiceInvite(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), me.Id, friendId, DateTimeOffset.UtcNow.AddMinutes(5));
        var metadata = DiscordVoiceProtocol.Metadata(_applicationId, invitation);
        var message = new Dictionary<string, string>(metadata) { ["steamy.voice_secret"] = invitation.Secret, ["steamy.client_nonce"] = Guid.NewGuid().ToString("N") };
        await DiscordPrivateCallFlow.StartAsync(
            ct => RequestAsync("CreateOrJoinLobbyWithMetadata", [invitation.Secret, metadata, new Dictionary<string, string>()], values => Convert.ToUInt64(values[1]), ct),
            (id, ct) => StartVoiceAsync(id, "Call with " + Snapshot.Relationships.First(friend => friend.Id == friendId).DisplayName, invitation, ct),
            ct => RequestAsync("SendUserMessageWithMetadata", [friendId, "Voice call invitation · Open Steamy to join.", message], values => Convert.ToUInt64(values[1]), ct),
            async id => { if (Voice.LobbyId == id) await LeaveVoiceCoreAsync(CancellationToken.None); else await LeaveUnusedVoiceLobbyAsync(id); }, token);
    }, token);
    public Task AcceptVoiceInviteAsync(DiscordVoiceInvite invitation, CancellationToken token = default) => VoiceMutationAsync(async () =>
    {
        if (invitation.Expired || invitation.GuestId != Snapshot.Profile?.Id || !Snapshot.Relationships.Any(friend => friend.Id == invitation.HostId && friend.IsFriend)) throw new DiscordSocialException("This call invitation has expired or isn't addressed to you.");
        await LeaveVoiceCoreAsync(token);
        var id = await RequestAsync("CreateOrJoinLobby", [invitation.Secret], values => Convert.ToUInt64(values[1]), token);
        try
        {
            var lobby = Invoke(_client, "GetLobbyHandle", id);
            if (lobby is null) throw new DiscordSocialException("This call has ended.");
            try
            {
                var actual = DiscordVoiceProtocol.Read(Call(lobby, "Metadata") as IDictionary, _applicationId, invitation.HostId, invitation.Secret);
                var members = ((IEnumerable)Call(lobby, "LobbyMemberIds")!).Cast<object>().Select(Convert.ToUInt64).ToArray();
                if (actual != invitation || !members.Contains(invitation.HostId) || members.Any(member => member != invitation.HostId && member != invitation.GuestId)) throw new DiscordSocialException("This private call is no longer available.");
            }
            finally { DisposeObject(lobby); }
            token.ThrowIfCancellationRequested(); await StartVoiceAsync(id, "Call with " + Snapshot.Relationships.First(friend => friend.Id == invitation.HostId).DisplayName, invitation, token);
        }
        catch { await LeaveUnusedVoiceLobbyAsync(id); throw; }
    }, token);
    private async Task StartVoiceAsync(ulong lobbyId, string label, DiscordVoiceInvite? invitation, CancellationToken token)
    {
        // Configure the call while globally muted, including when restored preferences use PTT.
        Invoke(_client, "SetSelfMuteAll", true);
        var call = Invoke(_client, "StartCall", lobbyId) ?? throw new DiscordSocialException("Voice couldn't start. Reconnect Discord and try again.");
        _call = call; _privateVoice = invitation; _voiceConnected = _privateHadPeer = false; _speaking.Clear();
        var generation = ++_voiceGeneration;
        var connection = new DiscordVoiceConnection();
        Voice = new(lobbyId, label, invitation is not null, "Connecting", _voiceOptions.Muted, _voiceOptions.Deafened, _voiceOptions.PushToTalk, []);
        try
        {
        void Callback(string method, Action<object?[]> handler)
        {
            var info = Method(call.GetType(), method, 1);
            var callback = MakeCallback(info.GetParameters()[0].ParameterType, values =>
            {
                if (generation != _voiceGeneration || _disposed || !ReferenceEquals(call, _call)) return;
                try { handler(values); } catch { connection.Update("Disconnected", "VoiceConnectionFailed", 0); _ = EndFailedVoiceAsync("Voice state couldn't be read. Rejoin to continue."); }
            });
            _voiceCallbacks.Add(callback); info.Invoke(call, [callback]);
        }
        Callback("SetStatusChangedCallback", values =>
        {
            var state = values[0]?.ToString() ?? "Disconnected";
            var error = values[1]?.ToString() ?? "None";
            var detail = values.Length > 2 ? Convert.ToInt32(values[2]) : 0;
            connection.Update(state, error, detail);
            if (state is "Connecting" or "SignalingConnected" or "Connected" or "Reconnecting") _voiceConnected = true;
            Voice = Voice with { State = state }; VoiceChanged?.Invoke(this, EventArgs.Empty);
            if (state == "Disconnected" && (_voiceConnected || error != "None")) _ = EndFailedVoiceAsync(DiscordVoiceConnection.ErrorMessage(error, detail));
        });
        Callback("SetSpeakingStatusChangedCallback", values => { var id = Convert.ToUInt64(values[0]); if (Convert.ToBoolean(values[1])) _speaking.Add(id); else _speaking.Remove(id); RefreshVoiceParticipants(); });
        Callback("SetParticipantChangedCallback", _ => RefreshVoiceParticipants());
        Callback("SetOnVoiceStateChangedCallback", _ => RefreshVoiceParticipants());
        ApplyCallOptions(); RefreshVoiceParticipants();
        _pushToTalk ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _pushToTalk.Tick -= PushToTalkTick; _pushToTalk.Tick += PushToTalkTick; _pushToTalk.Start();
        VoiceChanged?.Invoke(this, EventArgs.Empty);
        connection.Update(Call(call, "GetStatus")?.ToString() ?? "Joining", "None", 0);
        await connection.WaitAsync(token);
        }
        catch
        {
            try { await LeaveVoiceCoreAsync(CancellationToken.None); } catch { }
            throw;
        }
    }
    private void PushToTalkTick(object? sender, EventArgs args)
    {
        if (_call is null) return;
        var active = _voiceOptions.PushToTalk && !_voiceOptions.Muted && !_voiceOptions.Deafened && (GetAsyncKeyState(_voiceOptions.PushToTalkKey) & 0x8000) != 0;
        if (active != _pttActive) { _pttActive = active; Invoke(_call, "SetPTTActive", active); }
        if (_privateVoice is { Expired: true } && Voice.Participants.All(participant => participant.Profile.Id == Snapshot.Profile?.Id)) _ = EndFailedVoiceAsync("The unanswered call invitation expired.");
    }
    private void ApplyCallOptions()
    {
        if (_call is null) return;
        Invoke(_client, "SetSelfMuteAll", true);
        Invoke(_call, "SetAudioMode", Enum.Parse(Type("AudioModeType"), _voiceOptions.PushToTalk ? "MODE_PTT" : "MODE_VAD"));
        Invoke(_call, "SetPTTActive", false); _pttActive = false;
        Invoke(_call, "SetSelfMute", _voiceOptions.Muted); Invoke(_call, "SetSelfDeaf", _voiceOptions.Deafened);
        Invoke(_client, "SetSelfDeafAll", _voiceOptions.Deafened); Invoke(_client, "SetSelfMuteAll", _voiceOptions.Muted);
        Voice = Voice with { Muted = _voiceOptions.Muted, Deafened = _voiceOptions.Deafened, PushToTalk = _voiceOptions.PushToTalk };
    }
    private void RefreshVoiceParticipants()
    {
        if (_call is null) return;
        var participants = new List<DiscordVoiceParticipant>();
        foreach (var id in ((IEnumerable)Call(_call, "GetParticipants")!).Cast<object>().Select(Convert.ToUInt64).Take(25))
        {
            if (_privateVoice is { } privateCall && id != privateCall.HostId && id != privateCall.GuestId) { _ = EndFailedVoiceAsync("This private call's membership changed. Start a new call."); return; }
            if (_privateVoice is not null && id != Snapshot.Profile?.Id) _privateHadPeer = true;
            var user = Invoke(_client, "GetUser", id); DiscordProfile? profile;
            try { profile = Profile(user); } finally { DisposeObject(user); }
            if (profile is null) continue;
            var state = Call(_call, "GetVoiceStateHandle", id); var muted = false;
            try { muted = state is not null && Convert.ToBoolean(Call(state, "SelfMute")); } finally { DisposeObject(state); }
            participants.Add(new(profile, _speaking.Contains(id), muted, Convert.ToSingle(Call(_call, "GetParticipantVolume", id))));
        }
        Voice = Voice with { Participants = participants }; VoiceChanged?.Invoke(this, EventArgs.Empty);
        if (_privateVoice is not null && _privateHadPeer && participants.All(participant => participant.Profile.Id == Snapshot.Profile?.Id)) _ = EndFailedVoiceAsync("The other person left the call.");
    }
    private void RefreshPrivateVoiceMembership()
    {
        if (_privateVoice is not { } invitation || _call is null) return;
        var lobby = Invoke(_client, "GetLobbyHandle", Voice.LobbyId);
        if (lobby is null) { _ = EndFailedVoiceAsync("This private call has ended."); return; }
        try
        {
            var members = ((IEnumerable)Call(lobby, "LobbyMemberIds")!).Cast<object>().Select(Convert.ToUInt64).Take(3).ToArray();
            if (!members.Contains(invitation.HostId) || members.Any(id => id != invitation.HostId && id != invitation.GuestId)
                || _privateHadPeer && !members.Contains(invitation.GuestId)) _ = EndFailedVoiceAsync("This private call's membership changed. Start a new call.");
        }
        finally { DisposeObject(lobby); }
    }
    private async Task EndFailedVoiceAsync(string reason)
    {
        var generation = _voiceGeneration;
        try { await VoiceMutationAsync(async () =>
        { if (_voiceGeneration != generation) return; await LeaveVoiceCoreAsync(CancellationToken.None); Voice = DiscordVoiceSnapshot.Empty with { State = reason }; VoiceChanged?.Invoke(this, EventArgs.Empty); }, CancellationToken.None); }
        catch { }
    }
    public Task LeaveVoiceAsync(CancellationToken token = default) => VoiceMutationAsync(() => LeaveVoiceCoreAsync(token), token);
    private async Task LeaveVoiceCoreAsync(CancellationToken token)
    {
        if (_call is null) return;
        var call = _call; var id = Voice.LobbyId; var privateCall = _privateVoice is not null;
        Invoke(call, "SetSelfMute", true); Invoke(call, "SetPTTActive", false);
        _pushToTalk?.Stop(); _call = null; _privateVoice = null; _voiceGeneration++; _speaking.Clear();
        Voice = DiscordVoiceSnapshot.Empty; VoiceChanged?.Invoke(this, EventArgs.Empty);
        try { await RequestAsync("EndCall", [id], _ => true, token, false).WaitAsync(TimeSpan.FromSeconds(5), token); }
        finally { DisposeObject(call); _voiceCallbacks.Clear(); if (privateCall) await LeaveUnusedVoiceLobbyAsync(id); }
    }
    private async Task LeaveUnusedVoiceLobbyAsync(ulong id)
    { try { await ResultAsync("LeaveLobby", [id], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    public async Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default)
    {
        var inputs = await RequestAsync("GetInputDevices", [], values => ReadAudioDevices(values[0]), token, false);
        var outputs = await RequestAsync("GetOutputDevices", [], values => ReadAudioDevices(values[0]), token, false);
        return new(inputs, outputs);
    }
    private static IReadOnlyList<DiscordAudioDevice> ReadAudioDevices(object? native)
    {
        var devices = new List<DiscordAudioDevice>();
        if (native is not IEnumerable values) return devices;
        foreach (var device in values)
        {
            try { var id = Call(device, "Id") as string ?? ""; var name = Call(device, "Name") as string ?? "Audio device";
                if (devices.Count < 64 && id.Length <= 1024 && name.Length <= 240) devices.Add(new(id, name, Convert.ToBoolean(Call(device, "IsDefault")))); }
            finally { DisposeObject(device); }
        }
        return devices;
    }
    public async Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (options.InputId is null or { Length: > 1024 } || options.OutputId is null or { Length: > 1024 } || !float.IsFinite(options.InputVolume) || !float.IsFinite(options.OutputVolume) || options.InputVolume is < 0 or > 200 || options.OutputVolume is < 0 or > 200 || options.PushToTalkKey is < 1 or > 254) throw new ArgumentException("Invalid voice settings.");
        _requestedVoiceOptions = options;
        // Muting is immediate even while a previous device switch is awaiting its callback.
        _voiceOptions = options with { InputId = _voiceOptions.InputId, OutputId = _voiceOptions.OutputId }; ApplyCallOptions();
        await _voiceDeviceUpdate.WaitAsync(token);
        try
        {
            var requested = _requestedVoiceOptions;
            if (requested.InputId.Length > 0 && requested.InputId != _voiceOptions.InputId)
            { await ResultAsync("SetInputDevice", [requested.InputId], token); _voiceOptions = _voiceOptions with { InputId = requested.InputId }; }
            if (requested.OutputId.Length > 0 && requested.OutputId != _voiceOptions.OutputId)
            { await ResultAsync("SetOutputDevice", [requested.OutputId], token); _voiceOptions = _voiceOptions with { OutputId = requested.OutputId }; }
            Invoke(_client, "SetInputVolume", _requestedVoiceOptions.InputVolume); Invoke(_client, "SetOutputVolume", _requestedVoiceOptions.OutputVolume);
            VoiceChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _voiceDeviceUpdate.Release(); }
    }
    public Task SetParticipantVolumeAsync(ulong userId, float volume, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_call is null || !Voice.Participants.Any(participant => participant.Profile.Id == userId) || !float.IsFinite(volume) || volume is < 0 or > 200) throw new ArgumentException("Choose a current voice participant and volume from 0 to 200%.");
        Invoke(_call, "SetParticipantVolume", userId, volume); RefreshVoiceParticipants(); return Task.CompletedTask;
    }
    private void DisposeVoice()
    {
        _pushToTalk?.Stop(); _voiceGeneration++;
        if (_call is not null) { try { Invoke(_call, "SetSelfMute", true); Invoke(_call, "SetPTTActive", false); } catch { } DisposeObject(_call); _call = null; }
        _voiceCallbacks.Clear(); _privateVoice = null; Voice = DiscordVoiceSnapshot.Empty;
    }
}
