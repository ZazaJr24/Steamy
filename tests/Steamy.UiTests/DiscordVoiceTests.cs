using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class DiscordWorkspaceTests
{
    [Fact]
    public async Task ThePhoneActionShowsPendingVoiceImmediatelyAndCanBeCancelled()
    {
        var service = new SocialFixture { VoiceStartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var voice = new DiscordVoiceViewModel(service, new Settings());
        var pending = voice.StartCallCommand.ExecuteAsync(service.Snapshot.Relationships[0]);
        Assert.False(voice.Active); Assert.Equal(System.Windows.Visibility.Visible, voice.BarVisibility);
        Assert.Equal("Preparing connection…", voice.State); Assert.Equal("Cancel", voice.LeaveLabel);
        Assert.True(voice.LeaveCommand.CanExecute(null)); await voice.LeaveCommand.ExecuteAsync(null); await pending;
        Assert.False(voice.Active); Assert.Equal(System.Windows.Visibility.Collapsed, voice.BarVisibility);
        Assert.Contains("cancelled", voice.Status);
    }
    [Fact]
    public async Task VoiceNeverJoinsOnRestorationAndOnlyExplicitActionsStartAudio()
    {
        var service = new SocialFixture(); var settings = new Settings();
        settings.Load().VoiceOptions = new(PushToTalk: true, Muted: true);
        using var voice = new DiscordVoiceViewModel(service, settings);
        using var workspace = new DiscordFriendsViewModel(service, settings, voice: voice);
        workspace.SelectedFriend = workspace.Friends[0];
        await service.RestoreAsync(); service.ChangePresence(2, "Idle");
        Assert.Empty(service.VoiceOperations);
        Assert.False(voice.Active); Assert.True(voice.Muted); Assert.True(voice.PushToTalk);
        await voice.StartCallCommand.ExecuteAsync(workspace.SelectedFriend);
        Assert.Equal(["options", "call:2"], service.VoiceOperations);
        Assert.True(voice.Active); Assert.Equal("Switch and call this friend", voice.StartCallLabel);
        workspace.SelectedFriend = workspace.Friends[1];
        Assert.True(voice.Active); // Conversation/page selection does not own the call.
        voice.MuteCommand.Execute(null);
        Assert.False(service.AppliedVoiceOptions!.Muted);
        await voice.LeaveCommand.ExecuteAsync(null);
        Assert.False(voice.Active); Assert.Equal("leave", service.VoiceOperations.Last());
    }
    [Fact]
    public async Task VoiceDeviceFailureDoesNotAccidentallyJoinAndExpiredOrOwnInvitesAreDisabled()
    {
        var service = new SocialFixture { VoiceOptionsError = new DiscordSocialException("That microphone is no longer connected.") };
        using var voice = new DiscordVoiceViewModel(service, new Settings());
        await voice.StartCallCommand.ExecuteAsync(service.Snapshot.Relationships[0]);
        Assert.False(voice.Active); Assert.Equal(["options"], service.VoiceOperations);
        Assert.Contains("microphone", voice.Status);
        var invitation = new DiscordVoiceInvite(new string('A', 64), 2, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
        var message = Message(8, 2, "Call") with { VoiceInvite = invitation };
        Assert.False(voice.AcceptCallCommand.CanExecute(message));
        Assert.False(voice.AcceptCallCommand.CanExecute(message with { IsOwn = true, VoiceInvite = invitation with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) } }));
    }
    [Fact]
    public void VoiceInvitesAreTypedAndBoundToTheirApplicationAndActualSender()
    {
        var invite = new DiscordVoiceInvite(new string('B', 64), 2, 1, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
        var metadata = DiscordVoiceProtocol.Metadata(42, invite);
        metadata["steamy.voice_secret"] = invite.Secret;
        Assert.Equal(invite, DiscordVoiceProtocol.Read(metadata, 42, 2));
        Assert.Null(DiscordVoiceProtocol.Read(metadata, 43, 2));
        Assert.Null(DiscordVoiceProtocol.Read(metadata, 42, 3));
        metadata["steamy.kind"] = "party"; Assert.Null(DiscordVoiceProtocol.Read(metadata, 42, 2));
        metadata["steamy.kind"] = "private_voice"; metadata["steamy.expires"] = long.MaxValue.ToString();
        Assert.Null(DiscordVoiceProtocol.Read(metadata, 42, 2));
    }
    private sealed partial class SocialFixture : IDiscordVoiceService
    {
        public DiscordVoiceSnapshot Voice { get; private set; } = DiscordVoiceSnapshot.Empty;
        public event EventHandler? VoiceChanged;
        public List<string> VoiceOperations = [];
        public DiscordVoiceOptions? AppliedVoiceOptions;
        public Exception? VoiceOptionsError;
        public TaskCompletionSource? VoiceStartGate;
        private void VoiceState(DiscordVoiceSnapshot state) { Voice = state; VoiceChanged?.Invoke(this, EventArgs.Empty); }
        public Task JoinPartyVoiceAsync(CancellationToken token = default) { VoiceOperations.Add("party"); VoiceState(new(Party!.Id, "Party voice", false, "Connected", false, false, false, [])); return Task.CompletedTask; }
        public async Task StartPrivateCallAsync(ulong friend, CancellationToken token = default) { VoiceOperations.Add("call:" + friend); if (VoiceStartGate is not null) await VoiceStartGate.Task.WaitAsync(token); VoiceState(new(20, "Call", true, "Connected", false, false, false, [])); }
        public Task AcceptVoiceInviteAsync(DiscordVoiceInvite invitation, CancellationToken token = default) => StartPrivateCallAsync(invitation.HostId, token);
        public Task LeaveVoiceAsync(CancellationToken token = default) { VoiceOperations.Add("leave"); VoiceState(DiscordVoiceSnapshot.Empty); return Task.CompletedTask; }
        public Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default) => Task.FromResult(new DiscordAudioDevices([new("mic", "Microphone", true)], [new("speaker", "Speakers", true)]));
        public Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default) { VoiceOperations.Add("options"); if (VoiceOptionsError is not null) throw VoiceOptionsError; AppliedVoiceOptions = options; return Task.CompletedTask; }
        public Task SetParticipantVolumeAsync(ulong id, float volume, CancellationToken token = default) => Task.CompletedTask;
    }
}
