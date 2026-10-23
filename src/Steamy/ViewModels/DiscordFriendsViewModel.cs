using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class DiscordFriendsViewModel : ObservableObject, IDisposable
{
    private readonly IDiscordSocialService _discord;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private CancellationTokenSource? _operation;
    private bool _isBusy;
    private bool _isError;
    private string _message = "";
    private string _search = "";
    private string _filter = "Friends";
    private string _newFriend = "";
    private bool _wasConnected;
    private Task? _initialization;
    private bool _isRestoring;
    private int _refreshQueued;
    internal Func<TimeSpan, CancellationToken, Task> ReconnectDelay = Task.Delay;
    public bool IsRestoring { get => _isRestoring; private set { if (SetProperty(ref _isRestoring, value)) { OnPropertyChanged(nameof(FriendSummary)); OnPropertyChanged(nameof(ConnectionState)); OnPropertyChanged(nameof(ProfileLabel)); } } }
    public string ConnectionState => IsConnected ? "Connected to Discord" : IsRestoring ? "Connecting to Discord…" : "Discord disconnected";
    public ObservableCollection<DiscordFriend> Friends { get; } = [];
    public string[] Filters { get; } = ["Friends", "Requests", "Blocked"];
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand SendRequestCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> AcceptCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> RejectCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> CancelRequestCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> RemoveCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> BlockCommand { get; }
    public IAsyncRelayCommand<DiscordFriend> UnblockCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RefreshCommand { get; }

    public DiscordFriendsViewModel(IDiscordSocialService discord, ISettingsService settings,
        IAppDataStore? store = null, IGameLaunchService? gameLaunch = null, NotificationCenterViewModel? notifications = null, IArtworkService? artwork = null, DiscordVoiceViewModel? voice = null)
    {
        _discord = discord;
        Voice = voice;
        _settings = settings.Load();
        _discord.Changed += DiscordChanged;
        _settings.PropertyChanged += SettingsChanged;
        ConnectCommand = new AsyncRelayCommand(() => RunAsync(ConnectOrResumeAsync, "Connected to Discord.", "Connecting to Discord…"), () => CanConnect);
        DisconnectCommand = new AsyncRelayCommand(() => RunAsync(_discord.DisconnectAsync, "Disconnected. Your saved Discord session was removed."), () => (IsConnected || _discord.HasSavedSession) && !IsBusy);
        SendRequestCommand = new AsyncRelayCommand(SendRequestAsync, () => IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(NewFriend));
        AcceptCommand = FriendCommand(DiscordFriendAction.Accept);
        RejectCommand = FriendCommand(DiscordFriendAction.Reject);
        CancelRequestCommand = FriendCommand(DiscordFriendAction.Cancel);
        RemoveCommand = FriendCommand(DiscordFriendAction.Remove);
        BlockCommand = FriendCommand(DiscordFriendAction.Block);
        UnblockCommand = FriendCommand(DiscordFriendAction.Unblock);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        RefreshCommand = new RelayCommand(Refresh);
        _socialArtwork = artwork;
        InitializeWorkspace(store, gameLaunch);
        InitializeNotifications(notifications);
        Refresh();
    }

    public bool IsConnected => (_discord.Snapshot ?? DiscordSnapshot.Disconnected).IsConnected;
    public DiscordVoiceViewModel? Voice { get; }
    public bool CanConnect => _discord.IsAvailable && !IsBusy && (!IsConnected || !HasCommunicationPermission);
    public string ConnectLabel => IsConnected ? "Enable chat & parties" : _discord.HasSavedSession ? "Reconnect" : "Connect Discord";
    public Visibility ConnectionActionVisibility => CanConnect || !IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public bool CanEditApplication => !IsBusy && !IsConnected && !_discord.HasSavedSession;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); } }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }
    public string StatusMessage { get => _message; private set => SetProperty(ref _message, value); }
    public string ProfileLabel => _discord.Snapshot?.Profile?.DisplayName ?? (IsRestoring ? "Connecting Discord" : "Connect your Discord");
    public string AccountLabel => _discord.Snapshot?.Profile is { } profile ? "@" + profile.Username : "Discord · optional";
    public string? AvatarUrl => _discord.Snapshot?.Profile?.AvatarUrl;
    public string FriendSummary => IsConnected
        ? $"{_discord.Snapshot.Relationships.Count(f => f.IsFriend)} friends · {_discord.Snapshot.Relationships.Count(f => f.IsOnline)} online · {_discord.Snapshot.Relationships.Count(f => f.IsIncoming)} incoming"
        : IsRestoring ? "Connecting your saved Discord account…" : "Your friends, messages and next session.";
    public string EmptyTitle => !IsConnected ? "Bring your people along" : SearchText.Length > 0 ? "No matching people"
        : SelectedFilter == "Requests" ? "All caught up" : SelectedFilter == "Blocked" ? "No blocked users" : "Your next teammate starts here";
    public string EmptyDescription => !IsConnected ? "Find each other through Discord, with or without a Steam account."
        : SearchText.Length > 0 ? "Try another username or display name."
        : SelectedFilter == "Requests" ? "New incoming and outgoing friend requests will appear here."
        : SelectedFilter == "Blocked" ? "People you block on Discord will appear here."
        : "Add someone by their Discord username or user ID.";
    public Visibility ConnectedVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DisconnectedVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DisconnectVisibility => IsConnected || _discord.HasSavedSession ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) Refresh(); } }
    public string SelectedFilter { get => _filter; set { if (SetProperty(ref _filter, value)) Refresh(); } }
    public string NewFriend { get => _newFriend; set { if (SetProperty(ref _newFriend, value)) SendRequestCommand.NotifyCanExecuteChanged(); } }

    public Task InitializeAsync()
    {
        if (IsConnected || !_settings.DiscordAutoConnect) return Task.CompletedTask;
        return _initialization ??= RestoreAtStartupAsync();
    }
    private async Task RestoreAtStartupAsync()
    {
        IsRestoring = true;
        try
        {
            await RunAsync(async token =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { await _discord.RestoreAsync(token); return; }
                    catch (DiscordAuthorizationException) { throw; }
                    catch (Exception error) when (attempt < 2 && _discord.HasSavedSession && error is DiscordSocialException or TimeoutException)
                    { StatusMessage = "Reconnecting to Discord…"; await ReconnectDelay(TimeSpan.FromSeconds(attempt == 0 ? 2 : 6), token); }
                }
            }, null, "Connecting your saved Discord account…");
        }
        finally { IsRestoring = false; }
    }
    private async Task ConnectOrResumeAsync(CancellationToken token)
    {
        if (!IsConnected)
        {
            try { await _discord.RestoreAsync(token); }
            catch (DiscordAuthorizationException) { }
            if (IsConnected && HasCommunicationPermission) return;
        }
        await _discord.ConnectAsync(token);
    }

    private async Task SendRequestAsync()
    {
        var entered = NewFriend.Trim().TrimStart('@');
        if (_discord.Snapshot.Profile is { } me && (entered == me.Id.ToString() || entered.Equals(me.Username, StringComparison.OrdinalIgnoreCase)))
        { IsError = true; StatusMessage = "That's your own account. Enter your friend's Discord username."; return; }
        await RunAsync(async token => { await _discord.SendFriendRequestAsync(entered, token); NewFriend = ""; }, "Friend request sent. It will appear in Requests.");
    }

    private IAsyncRelayCommand<DiscordFriend> FriendCommand(DiscordFriendAction action) =>
        new AsyncRelayCommand<DiscordFriend>(friend => friend is null ? Task.CompletedTask
            : RunAsync(token => _discord.UpdateFriendAsync(friend, action, token), action switch
            {
                DiscordFriendAction.Accept => "Friend request accepted.", DiscordFriendAction.Reject => "Friend request declined.",
                DiscordFriendAction.Cancel => "Friend request cancelled.", DiscordFriendAction.Remove => "Friend removed.",
                DiscordFriendAction.Block => "User blocked on Discord.", _ => "User unblocked on Discord."
            }), friend => friend is not null && IsConnected && !IsBusy);

    private async Task RunAsync(Func<CancellationToken, Task> action, string? success, string? progress = null)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        IsBusy = true; IsError = false;
        StatusMessage = progress ?? "Working… You can cancel at any time.";
        try
        {
            await action(cancellation.Token);
            StatusMessage = success ?? (IsConnected ? "Your Discord friends are up to date." : _discord.AvailabilityMessage);
        }
        catch (OperationCanceledException)
        { StatusMessage = cancellation.IsCancellationRequested ? "Cancelled. You can connect later." : "Discord didn't respond in time. Please try again."; }
        catch (Exception exception) when (exception is DiscordSocialException or InvalidOperationException or ArgumentException or TimeoutException)
        { IsError = true; StatusMessage = exception is TimeoutException ? "Discord didn't respond in time. Check your requests before trying again." : exception.Message; }
        catch
        { IsError = true; StatusMessage = "Discord is unavailable right now. Please try connecting again."; }
        finally { _operation = null; IsBusy = false; Refresh(); }
    }

    private void DiscordChanged(object? sender, EventArgs args)
    {
        void Update()
        {
            if (_wasConnected && !IsConnected && !IsBusy)
            { IsError = true; StatusMessage = "Discord lost its connection. Reconnect to update your friends."; }
            Refresh();
        }
        if (_context is not null && SynchronizationContext.Current != _context)
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
                _context.Post(_ => { Interlocked.Exchange(ref _refreshQueued, 0); Update(); }, null);
        }
        else Update();
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettings.DiscordAutoConnect)) { if (!_settings.DiscordAutoConnect && IsRestoring) _operation?.Cancel(); return; }
        if (args.PropertyName != nameof(AppSettings.DiscordApplicationId)) return;
        if (!IsBusy && !IsConnected) StatusMessage = _discord.AvailabilityMessage;
        RefreshCommands();
    }
    private void Refresh()
    {
        var snapshot = _discord.Snapshot ?? DiscordSnapshot.Disconnected;
        _wasConnected = snapshot.IsConnected;
        var visible = snapshot.Relationships.Where(f => SelectedFilter switch
        { "Requests" => f.IsIncoming || f.IsOutgoing, "Blocked" => f.IsBlocked, _ => f.IsFriend });
        var query = SearchText.Trim();
        if (query.Length > 0) visible = visible.Where(f => f.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || f.Username.Contains(query, StringComparison.OrdinalIgnoreCase) || f.Id.ToString().Contains(query, StringComparison.Ordinal));
        var next = visible.ToArray();
        // Keep existing rows/selection in place when a presence update arrives.
        _updatingFriends = true;
        try
        {
            var nextIds = next.Select(friend => friend.Id).ToHashSet();
            for (var index = Friends.Count - 1; index >= 0; index--)
                if (!nextIds.Contains(Friends[index].Id)) Friends.RemoveAt(index);
            for (var index = 0; index < next.Length; index++)
            {
                if (index >= Friends.Count || Friends[index].Id != next[index].Id)
                {
                    var position = index + 1;
                    while (position < Friends.Count && Friends[position].Id != next[index].Id) position++;
                    if (position < Friends.Count) Friends.Move(position, index);
                    else Friends.Insert(index, next[index]);
                }
                if (Friends[index] != next[index]) Friends[index] = next[index];
            }
        }
        finally { _updatingFriends = false; }
        RefreshWorkspace();
        RefreshSocialNotifications();
        if (StatusMessage.Length == 0) StatusMessage = _discord.AvailabilityMessage;
        foreach (var property in new[] { nameof(IsConnected), nameof(ProfileLabel), nameof(AccountLabel), nameof(AvatarUrl), nameof(FriendSummary),
            nameof(ConnectedVisibility), nameof(DisconnectedVisibility), nameof(DisconnectVisibility), nameof(ConnectionState), nameof(EmptyTitle), nameof(EmptyDescription), nameof(EmptyVisibility) }) OnPropertyChanged(property);
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(ConnectLabel)); OnPropertyChanged(nameof(ConnectionActionVisibility));
        OnPropertyChanged(nameof(CanEditApplication));
        foreach (var command in new IRelayCommand[] { ConnectCommand, DisconnectCommand, SendRequestCommand, AcceptCommand, RejectCommand,
            CancelRequestCommand, RemoveCommand, BlockCommand, UnblockCommand, CancelCommand }) command?.NotifyCanExecuteChanged();
        RefreshWorkspaceCommands(); OnPropertyChanged(nameof(CanChooseGame));
    }
    public void Dispose() { DisposeNotifications(); DisposeWorkspace(); _operation?.Cancel(); _discord.Changed -= DiscordChanged; _settings.PropertyChanged -= SettingsChanged; }
}
