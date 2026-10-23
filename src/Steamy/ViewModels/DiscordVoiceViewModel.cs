using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class DiscordVoiceViewModel : ObservableObject, IDisposable
{
    private readonly IDiscordSocialService _social;
    private readonly IDiscordVoiceService? _voice;
    private readonly ISettingsService _settings;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private DiscordVoiceOptions _options;
    private bool _busy, _devicesLoading, _starting;
    private string _status = "";
    private int _refreshQueued;
    private DiscordAudioDevice? _input, _output;
    public ObservableCollection<DiscordAudioDevice> Inputs { get; } = [];
    public ObservableCollection<DiscordAudioDevice> Outputs { get; } = [];
    public ObservableCollection<VoiceParticipantViewModel> Participants { get; } = [];
    public DiscordVoiceSnapshot Snapshot => _voice?.Voice ?? DiscordVoiceSnapshot.Empty;
    public bool Active => Snapshot.Active;
    public Visibility BarVisibility => Active || _busy && _starting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PartyVoiceVisibility => Active && !Snapshot.IsPrivate && Snapshot.LobbyId == (_social as IDiscordChatService)?.Party?.Id ? Visibility.Visible : Visibility.Collapsed;
    public string Label => Active ? Snapshot.Label : "Discord voice";
    public string State => !Active && _busy && _starting ? "Preparing connection…" : Snapshot.State;
    public string LeaveLabel => _busy && _starting ? "Cancel" : "Leave call";
    public bool Available => _voice is not null && _social.Snapshot.IsConnected && (_social as IDiscordChatService)?.HasCommunicationPermission == true;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string JoinPartyLabel => Active && Snapshot.LobbyId != (_social as IDiscordChatService)?.Party?.Id ? "Switch to party voice" : Active ? "In party voice" : "Join party voice";
    public string AcceptLabel => Active ? "Switch and join call" : "Join call";
    public string StartCallLabel => Active ? "Switch and call this friend" : "Start a voice call";
    public string MuteLabel => Muted ? "Unmute" : "Mute";
    public string DeafLabel => Deafened ? "Undeafen" : "Deafen";
    public string TalkHint => PushToTalk ? "Hold " + KeyInterop.KeyFromVirtualKey(_options.PushToTalkKey) + " to talk" : "Voice activity";
    public bool Muted { get => _options.Muted; set => ChangeOptions(_options with { Muted = value }); }
    public bool Deafened { get => _options.Deafened; set => ChangeOptions(_options with { Deafened = value }); }
    public bool PushToTalk { get => _options.PushToTalk; set => ChangeOptions(_options with { PushToTalk = value }); }
    public float InputVolume { get => _options.InputVolume; set => ChangeOptions(_options with { InputVolume = Math.Clamp(value, 0, 200) }); }
    public float OutputVolume { get => _options.OutputVolume; set => ChangeOptions(_options with { OutputVolume = Math.Clamp(value, 0, 200) }); }
    public int PushToTalkKey { get => _options.PushToTalkKey; set => ChangeOptions(_options with { PushToTalkKey = value }); }
    public KeyValuePair<int, string>[] TalkKeys { get; } = [new(0x77, "F8"), new(0x78, "F9"), new(0x79, "F10"), new(0x56, "V")];
    public DiscordAudioDevice? SelectedInput { get => _input; set { if (SetProperty(ref _input, value) && value is not null && !_devicesLoading) ChangeOptions(_options with { InputId = value.Id }); } }
    public DiscordAudioDevice? SelectedOutput { get => _output; set { if (SetProperty(ref _output, value) && value is not null && !_devicesLoading) ChangeOptions(_options with { OutputId = value.Id }); } }
    public IAsyncRelayCommand JoinPartyCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> StartCallCommand { get; }
    public IAsyncRelayCommand<DiscordChatMessage> AcceptCallCommand { get; }
    public IAsyncRelayCommand LeaveCommand { get; }
    public IRelayCommand MuteCommand { get; }
    public IRelayCommand DeafenCommand { get; }
    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public DiscordVoiceViewModel(IDiscordSocialService social, ISettingsService settings)
    {
        _social = social; _voice = social as IDiscordVoiceService; _settings = settings;
        _options = settings.Load().VoiceOptions ?? new();
        _options = _options with { InputId = _options.InputId ?? "", OutputId = _options.OutputId ?? "", InputVolume = float.IsFinite(_options.InputVolume) ? Math.Clamp(_options.InputVolume, 0, 200) : 100, OutputVolume = float.IsFinite(_options.OutputVolume) ? Math.Clamp(_options.OutputVolume, 0, 200) : 100, PushToTalkKey = _options.PushToTalkKey is >= 1 and <= 254 ? _options.PushToTalkKey : 0x77 };
        // Construction/restoration reads settings only; it never calls StartCall or opens the microphone.
        JoinPartyCommand = new AsyncRelayCommand(() => RunAsync(async token => { await ApplyOptionsAsync(token); await _voice!.JoinPartyVoiceAsync(token); }), () => Available && !_busy && (_social as IDiscordChatService)?.Party is not null && Snapshot.LobbyId != (_social as IDiscordChatService)?.Party?.Id);
        StartCallCommand = new AsyncRelayCommand<DiscordFriend>(friend => friend is null ? Task.CompletedTask : RunAsync(async token => { await ApplyOptionsAsync(token); await _voice!.StartPrivateCallAsync(friend.Id, token); }), friend => Available && !_busy && friend?.IsFriend == true && friend.Id != _social.Snapshot.Profile?.Id);
        AcceptCallCommand = new AsyncRelayCommand<DiscordChatMessage>(message => message?.VoiceInvite is not { } invite ? Task.CompletedTask : RunAsync(async token => { await ApplyOptionsAsync(token); await _voice!.AcceptVoiceInviteAsync(invite, token); }), message => Available && !_busy && message is { IsOwn: false, VoiceInvite: { Expired: false } });
        LeaveCommand = new AsyncRelayCommand(async () => { if (_busy) { _operation?.Cancel(); return; } await RunAsync(token => _voice!.LeaveVoiceAsync(token), starting: false); }, () => Available && (Active || _busy));
        MuteCommand = new RelayCommand(() => Muted = !Muted, () => Active);
        DeafenCommand = new RelayCommand(() => Deafened = !Deafened, () => Active);
        RefreshDevicesCommand = new AsyncRelayCommand(async () =>
        {
            if (!Available || _voice is null) return;
            try
            {
                var devices = await _voice.GetAudioDevicesAsync(_lifetime.Token); _devicesLoading = true;
                Inputs.Clear(); foreach (var input in devices.Inputs) Inputs.Add(input);
                Outputs.Clear(); foreach (var output in devices.Outputs) Outputs.Add(output);
                SelectedInput = Inputs.FirstOrDefault(input => input.Id == _options.InputId) ?? Inputs.FirstOrDefault(input => input.IsDefault) ?? Inputs.FirstOrDefault();
                SelectedOutput = Outputs.FirstOrDefault(output => output.Id == _options.OutputId) ?? Outputs.FirstOrDefault(output => output.IsDefault) ?? Outputs.FirstOrDefault();
                Status = Inputs.Count == 0 ? "No microphone was found. Connect a device and refresh." : "";
            }
            catch (Exception error) { Status = error is DiscordSocialException ? error.Message : "Couldn't load audio devices."; }
            finally { _devicesLoading = false; }
        }, () => Available);
        _social.Changed += Changed; if (_voice is not null) _voice.VoiceChanged += Changed;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, bool starting = true)
    {
        if (_busy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; _busy = true; _starting = starting; Status = ""; Refresh();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Status = "Voice was cancelled. Join again when you're ready."; }
        catch (Exception error) { Status = error is DiscordSocialException or InvalidOperationException ? error.Message : "Voice couldn't complete the request. Check your connection and audio devices."; }
        finally { _operation = null; _busy = _starting = false; Refresh(); }
    }
    private Task ApplyOptionsAsync(CancellationToken token) => _voice!.SetVoiceOptionsAsync(_options, token);
    private void ChangeOptions(DiscordVoiceOptions options)
    {
        if (options == _options) return; _options = options; NotifyOptions(); _ = SaveOptionsAsync(options);
    }
    private async Task SaveOptionsAsync(DiscordVoiceOptions options)
    {
        try
        {
            if (Available && _voice is not null) await _voice.SetVoiceOptionsAsync(options, _lifetime.Token);
            var settings = _settings.Load(); settings.VoiceOptions = _options; await _settings.SaveAsync(settings, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status = error is DiscordSocialException ? error.Message : "Couldn't apply these voice settings."; }
    }
    private void NotifyOptions()
    { foreach (var name in new[] { nameof(Muted), nameof(Deafened), nameof(PushToTalk), nameof(InputVolume), nameof(OutputVolume), nameof(PushToTalkKey), nameof(MuteLabel), nameof(DeafLabel), nameof(TalkHint) }) OnPropertyChanged(name); }
    private void Changed(object? sender, EventArgs args)
    {
        if (_context is not null && SynchronizationContext.Current != _context)
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) _context.Post(_ => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_lifetime.IsCancellationRequested) Refresh(); }, null);
        }
        else Refresh();
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(Active), nameof(Available), nameof(BarVisibility), nameof(PartyVoiceVisibility), nameof(Label), nameof(State), nameof(JoinPartyLabel), nameof(AcceptLabel), nameof(StartCallLabel), nameof(LeaveLabel) }) OnPropertyChanged(name);
        var participants = Snapshot.Participants;
        for (var index = Participants.Count - 1; index >= 0; index--) if (!participants.Any(item => item.Profile.Id == Participants[index].Id)) { Participants[index].Dispose(); Participants.RemoveAt(index); }
        foreach (var participant in participants)
        {
            var item = Participants.FirstOrDefault(item => item.Id == participant.Profile.Id);
            if (item is null) { item = new VoiceParticipantViewModel(_voice!, participant, _lifetime.Token); Participants.Add(item); } else item.Update(participant);
        }
        foreach (var command in new IRelayCommand[] { JoinPartyCommand, StartCallCommand, AcceptCallCommand, LeaveCommand, MuteCommand, DeafenCommand, RefreshDevicesCommand }) command?.NotifyCanExecuteChanged();
        if (!Active && !_busy && Status.Length == 0 && Snapshot.State != "Disconnected") Status = Snapshot.State;
    }
    public void Dispose()
    { _lifetime.Cancel(); _social.Changed -= Changed; if (_voice is not null) _voice.VoiceChanged -= Changed; foreach (var participant in Participants) participant.Dispose(); _lifetime.Dispose(); }
}

public sealed class VoiceParticipantViewModel : ObservableObject, IDisposable
{
    private readonly IDiscordVoiceService _voice;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _volumeChange;
    private DiscordVoiceParticipant _participant;
    private float _volume;
    public ulong Id => _participant.Profile.Id;
    public string Name => _participant.Profile.DisplayName;
    public bool Speaking => _participant.Speaking;
    public string Detail => _participant.Muted ? "Muted" : Speaking ? "Speaking" : "Listening";
    public float Volume { get => _volume; set { if (SetProperty(ref _volume, Math.Clamp(value, 0, 200))) _ = SetVolumeAsync(); } }
    public VoiceParticipantViewModel(IDiscordVoiceService voice, DiscordVoiceParticipant participant, CancellationToken lifetime)
    { _voice = voice; _lifetime = lifetime; _participant = participant; _volume = participant.Volume; }
    public void Update(DiscordVoiceParticipant participant)
    { _participant = participant; SetProperty(ref _volume, participant.Volume, nameof(Volume)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Speaking)); OnPropertyChanged(nameof(Detail)); }
    private async Task SetVolumeAsync()
    {
        _volumeChange?.Cancel(); _volumeChange?.Dispose(); var pending = _volumeChange = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        try { await Task.Delay(120, pending.Token); await _voice.SetParticipantVolumeAsync(Id, Volume, pending.Token); }
        catch (Exception) { /* Participant may have left while the slider was being changed. */ }
    }
    public void Dispose() { _volumeChange?.Cancel(); _volumeChange?.Dispose(); }
}
