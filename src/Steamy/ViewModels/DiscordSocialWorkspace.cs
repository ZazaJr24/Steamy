using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class DiscordFriendsViewModel
{
    private IDiscordChatService? _chat;
    private IAppDataStore? _store;
    private IGameLaunchService? _gameLaunch;
    private IArtworkService? _socialArtwork;
    private Game? _requestedArtwork;
    private CancellationTokenSource? _artworkCancellation;
    private DiscordFriend? _selectedFriend;
    private DiscordFriend? _inviteFriend;
    private Game? _selectedGame;
    private bool _partyChat;
    private bool _updatingFriends;
    private bool _chatLoading;
    private bool _showAddFriend;
    private bool _chatVisible;
    private string _draft = "";
    private string _chatStatus = "";
    private CancellationTokenSource? _historyCancellation;
    private readonly HashSet<ulong> _deletedMessages = [];
    private long _conversationGeneration;
    private readonly HashSet<ulong> _sentPartyInvites = [];
    private readonly Dictionary<string, string> _conversationDrafts = [];
    private ulong _invitePartyId;
    public ObservableCollection<DiscordChatMessage> Messages { get; } = [];
    public ObservableCollection<DiscordPartyInvite> PartyInvites { get; } = [];
    public ObservableCollection<Game> AvailableGames { get; } = [];
    public IAsyncRelayCommand SendMessageCommand { get; private set; } = null!;
    public IAsyncRelayCommand ReloadChatCommand { get; private set; } = null!;
    public IAsyncRelayCommand<DiscordChatMessage> OpenDiscordMessageCommand { get; private set; } = null!;
    public string ConversationKey => _partyChat ? "party:" + (Party?.Id ?? 0) : "friend:" + (SelectedFriend?.Id ?? 0);
    public IAsyncRelayCommand CreatePartyCommand { get; private set; } = null!;
    public IAsyncRelayCommand JoinPartyCodeCommand { get; private set; } = null!;
    public IRelayCommand CopyPartyCodeCommand { get; private set; } = null!;
    private string _partyJoinCode = "";
    public string PartyJoinCode { get => _partyJoinCode; set { if (SetProperty(ref _partyJoinCode, value)) JoinPartyCodeCommand?.NotifyCanExecuteChanged(); } }
    public string PartyCode => (_discord as IDiscordPartyCodes)?.PartyCode ?? "";
    public IAsyncRelayCommand InviteFriendCommand { get; private set; } = null!;
    public IAsyncRelayCommand<DiscordFriend> InvitePersonCommand { get; private set; } = null!;
    public IAsyncRelayCommand PlayWithFriendCommand { get; private set; } = null!;
    public IAsyncRelayCommand LeavePartyCommand { get; private set; } = null!;
    public IAsyncRelayCommand LaunchGameCommand { get; private set; } = null!;
    public IAsyncRelayCommand<DiscordPartyInvite> JoinInviteCommand { get; private set; } = null!;
    public IRelayCommand<DiscordPartyInvite> DismissInviteCommand { get; private set; } = null!;
    public IRelayCommand OpenPartyChatCommand { get; private set; } = null!;
    public IRelayCommand ToggleAddFriendCommand { get; private set; } = null!;
    public bool HasCommunicationPermission => _chat?.HasCommunicationPermission == true;
    public DiscordParty? Party => _chat?.Party;
    public IReadOnlyList<DiscordFriend> InviteTargets => _discord.Snapshot?.Relationships.Where(friend => friend.IsFriend).ToArray() ?? [];
    public string PartyTitle => Party?.Game.Name ?? SelectedGame?.Name ?? "Choose your next game";
    public string PartyDescription => Party is { } party ? $"{party.Members.Count} in your party" : "Choose a game and invite your friends.";
    public IReadOnlyList<DiscordProfile> PartyMembers => Party?.Members ?? [];
    public Visibility PartyVisibility => Party is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoPartyVisibility => Party is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InviteVisibility => PartyInvites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConversationVisibility => IsConnected && (SelectedFriend is { IsFriend: true } || _partyChat && Party is not null) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FriendMenuVisibility => SelectedFriend?.IsFriend == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoConversationVisibility => ConversationVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChatEmptyVisibility => Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddFriendVisibility => _showAddFriend ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadingChatVisibility => _chatLoading ? Visibility.Visible : Visibility.Collapsed;
    public string ChatTitle => _partyChat ? "Party chat" : SelectedFriend?.DisplayName ?? "Messages";
    public string ChatInitial => _partyChat ? "P" : SelectedFriend?.Initial ?? "S";
    public string? ChatAvatarUrl => _partyChat ? null : SelectedFriend?.AvatarUrl;
    public string PlayWithFriendLabel => Party is null ? "Play together" : "Invite to party";
    public string JoinInviteLabel => Party is null ? "Join party" : "Switch party";
    public string CompactPartyLabel => PartyInvites.Count > 0 ? $"{PartyInvites.Count} invite{(PartyInvites.Count == 1 ? "" : "s")}" : "Party";
    public string NavigationSocialTooltip => PartyInvites.Count > 0 ? $"Friends · {PartyInvites.Count} party invitation{(PartyInvites.Count == 1 ? "" : "s")}" : "Friends and parties";
    public IReadOnlyList<DiscordInvitePerson> InvitePeople => InviteTargets.OrderByDescending(friend => friend.IsOnline).ThenBy(friend => friend.DisplayName)
        .Select(friend => new DiscordInvitePerson(friend, PartyMembers.Any(member => member.Id == friend.Id), _sentPartyInvites.Contains(friend.Id))).ToArray();
    public string ChatSubtitle => _partyChat ? PartyTitle : SelectedFriend?.Detail ?? "Choose a friend to start a conversation.";
    public string ChatStatus { get => _chatStatus; private set => SetProperty(ref _chatStatus, value); }
    public string MessageDraft { get => _draft; set { if (SetProperty(ref _draft, value)) SendMessageCommand.NotifyCanExecuteChanged(); } }
    public DiscordFriend? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (_updatingFriends || value is null && _selectedFriend is not null && IsConnected) return;
            if (value is not null && !value.IsFriend) return;
            if (_selectedFriend == value) return;
            ConversationChanging?.Invoke(this, EventArgs.Empty);
            SaveDraft();
            if (!SetProperty(ref _selectedFriend, value)) return;
            _partyChat = false;
            MessageDraft = _conversationDrafts.GetValueOrDefault(ConversationKey, "");
            if (value is not null) InviteFriend = value;
            _ = LoadConversationAsync(); RefreshWorkspace();
        }
    }
    public DiscordFriend? InviteFriend { get => _inviteFriend; set { if (SetProperty(ref _inviteFriend, value)) InviteFriendCommand.NotifyCanExecuteChanged(); } }
    public Game? SelectedGame { get => _selectedGame; set { if (SetProperty(ref _selectedGame, value)) { OnPropertyChanged(nameof(PartyArtwork)); OnPropertyChanged(nameof(PartyGame)); OnPropertyChanged(nameof(PartyTitle)); RefreshPartyArtwork(); RefreshWorkspaceCommands(); } } }
    public Game? PartyGame => GameToLaunch;
    public System.Windows.Media.Imaging.BitmapImage? PartyArtwork => PartyGame?.HeaderImage;
    public bool CanChooseGame => Party is null && !IsBusy;
    private ulong ConversationId => _partyChat ? Party?.Id ?? 0 : SelectedFriend?.Id ?? 0;
    private Game? GameToLaunch => Party is { } party ? AvailableGames.FirstOrDefault(game => game.AppId == party.Game.AppId) : SelectedGame;
    public string LaunchLabel => GameToLaunch is null ? "Game not installed" : "Launch game";

    private void InitializeWorkspace(IAppDataStore? store, IGameLaunchService? gameLaunch)
    {
        _chat = _discord as IDiscordChatService; _store = store; _gameLaunch = gameLaunch;
        if (_chat is not null) _chat.MessageChanged += ChatMessageChanged;
        if (_store is not null) _store.Games.CollectionChanged += GamesChanged;
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => HasCommunicationPermission && ConversationId != 0 && !_sendingConversations.Contains(ConversationKey) && _outbox.Count < 64 && MessageDraft.Trim().Length is > 0 and <= 2000, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RetryMessageCommand = new AsyncRelayCommand<DiscordChatMessage>(RetryMessageAsync, message => message is { NeedsDeliveryAction: true } && HasCommunicationPermission && !_sendingConversations.Contains(MessageConversation(message)), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DismissLocalMessageCommand = new RelayCommand<DiscordChatMessage>(message => { if (message is { NeedsDeliveryAction: true }) { _outbox.Remove(message.ClientNonce); Messages.Remove(message); RefreshWorkspaceCommands(); } });
        ReloadChatCommand = new AsyncRelayCommand(LoadConversationAsync, () => HasCommunicationPermission && ConversationId != 0 && !_chatLoading);
        JoinPartyCodeCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        { await ((IDiscordPartyCodes)_discord).JoinPartyCodeAsync(PartyJoinCode, token); PartyJoinCode = ""; OpenPartyChat(); }, "You joined the party.", "Joining the party…"),
            () => _discord is IDiscordPartyCodes && HasCommunicationPermission && !IsBusy && Party is null && PartyJoinCode.Trim().Length > 0);
        CopyPartyCodeCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(PartyCode); StatusMessage = "Party code copied. Share it with your friends."; }
            catch (System.Runtime.InteropServices.ExternalException) { StatusMessage = "Clipboard is busy. Select and copy the party code."; }
        }, () => PartyCode.Length > 0);
        OpenDiscordMessageCommand = new AsyncRelayCommand<DiscordChatMessage>(message => message is null ? Task.CompletedTask : RunAsync(token => ((IDiscordMessageNavigation)_discord).OpenMessageAsync(message.Id, token), "Opened in Discord."), message => message is not null && _discord is IDiscordMessageNavigation && IsConnected && !IsBusy);
        CreatePartyCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        { if (_chat is null || SelectedGame is null) return; await _chat.CreatePartyAsync(new(SelectedGame.AppId, SelectedGame.Name), token); OpenPartyChat(); }, "Party ready. Invite a friend."),
            () => HasCommunicationPermission && !IsBusy && Party is null && SelectedGame is not null);
        InviteFriendCommand = new AsyncRelayCommand(() => InvitePersonAsync(InviteFriend!),
            () => HasCommunicationPermission && !IsBusy && Party is not null && InviteFriend?.IsFriend == true);
        InvitePersonCommand = new AsyncRelayCommand<DiscordFriend>(friend => friend is null ? Task.CompletedTask : InvitePersonAsync(friend),
            friend => HasCommunicationPermission && !IsBusy && Party is not null && friend?.IsFriend == true && !PartyMembers.Any(member => member.Id == friend.Id));
        PlayWithFriendCommand = new AsyncRelayCommand(() =>
        {
            var friend = SelectedFriend;
            return friend is null ? Task.CompletedTask : RunAsync(async token =>
            {
                if (Party is null) await _chat!.CreatePartyAsync(new(SelectedGame!.AppId, SelectedGame.Name), token);
                await _chat!.InviteToPartyAsync(friend.Id, token); _sentPartyInvites.Add(friend.Id);
            }, "Party invitation sent to " + friend.DisplayName + ".", "Preparing your party invitation…");
        }, () => HasCommunicationPermission && !IsBusy && SelectedFriend?.IsFriend == true && (Party is not null || SelectedGame is not null)
            && !PartyMembers.Any(member => member.Id == SelectedFriend.Id));
        LeavePartyCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        { await _chat!.LeavePartyAsync(token); _partyChat = false; ClearConversation(); }, "You left the party."), () => HasCommunicationPermission && !IsBusy && Party is not null);
        LaunchGameCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        { if (GameToLaunch is not { } game || _gameLaunch is null) return; var result = await _gameLaunch.LaunchAsync(game, token); if (!result.Succeeded) throw new InvalidOperationException(result.Message); }, "Game launch requested. Join your multiplayer session in the game."),
            () => _gameLaunch is not null && GameToLaunch is not null && !IsBusy);
        JoinInviteCommand = new AsyncRelayCommand<DiscordPartyInvite>(invite => invite is null ? Task.CompletedTask : RunAsync(async token =>
        {
            var switched = Party is not null;
            if (switched) await _chat!.LeavePartyAsync(token);
            try { await _chat!.AcceptPartyInviteAsync(invite.Id, token); }
            catch (Exception error) when (switched && error is DiscordSocialException or InvalidOperationException or ArgumentException)
            { throw new DiscordSocialException("You left the previous party, but this invitation couldn't be joined. " + error.Message); }
            OpenPartyChat();
        }, "You joined the party.", "Joining the party…"), invite => invite is not null && HasCommunicationPermission && !IsBusy);
        DismissInviteCommand = new RelayCommand<DiscordPartyInvite>(invite => { if (invite is not null) _chat?.DismissPartyInvite(invite.Id); RefreshWorkspace(); });
        OpenPartyChatCommand = new RelayCommand(OpenPartyChat, () => HasCommunicationPermission && Party is not null);
        ToggleAddFriendCommand = new RelayCommand(() => { _showAddFriend = !_showAddFriend; OnPropertyChanged(nameof(AddFriendVisibility)); });
        RefreshGames();
    }

    private Task InvitePersonAsync(DiscordFriend friend) => RunAsync(async token =>
    {
        await _chat!.InviteToPartyAsync(friend.Id, token); _sentPartyInvites.Add(friend.Id);
    }, "Party invitation sent to " + friend.DisplayName + ".", "Sending a party invitation…");

    private void OpenPartyChat()
    {
        if (Party is null) return;
        if (_partyChat) return;
        ConversationChanging?.Invoke(this, EventArgs.Empty);
        if (!_partyChat) SaveDraft();
        _selectedFriend = null; _partyChat = true; OnPropertyChanged(nameof(SelectedFriend));
        MessageDraft = _conversationDrafts.GetValueOrDefault(ConversationKey, "");
        _ = LoadConversationAsync(); RefreshWorkspace();
    }
    private void SaveDraft()
    {
        if (MessageDraft.Length == 0) _conversationDrafts.Remove(ConversationKey);
        else
        {
            _conversationDrafts[ConversationKey] = MessageDraft;
            while (_conversationDrafts.Count > 32) _conversationDrafts.Remove(_conversationDrafts.Keys.First());
        }
    }
    private async Task LoadConversationAsync()
    {
        // On explicit reload, retain the current viewport before the collection is cleared.
        ConversationChanging?.Invoke(this, EventArgs.Empty);
        var previousHistory = _historyCancellation; _historyCancellation = null; previousHistory?.Cancel();
        var generation = ++_conversationGeneration;
        var id = ConversationId; var party = _partyChat;
        MarkCurrentConversationRead();
        Messages.Clear(); _deletedMessages.Clear(); _chatLoading = false;
        foreach (var pending in _outbox.Values.Where(entry => entry.Key == ConversationKey).ToArray()) UpsertMessage(pending.Message);
        if (_chat is null || !HasCommunicationPermission || id == 0) { ChatStatus = HasCommunicationPermission ? "" : "Connect Discord to enable chat and parties."; RefreshWorkspace(); return; }
        using var cancellation = new CancellationTokenSource(); _historyCancellation = cancellation;
        _chatLoading = true; ChatStatus = "Loading conversation…"; RefreshWorkspace();
        try
        {
            var history = await _chat.GetMessagesAsync(id, party, cancellation.Token);
            if (generation != _conversationGeneration) return;
            // Merge messages which arrived live during the history request instead of discarding them.
            foreach (var message in history)
                if (!_deletedMessages.Contains(message.Id) && !Messages.Any(current => current.Id == message.Id)) UpsertMessage(message);
            ChatStatus = "";
        }
        catch (OperationCanceledException) { if (generation == _conversationGeneration) ChatStatus = "Conversation loading cancelled."; }
        catch (Exception exception) { if (generation == _conversationGeneration) ChatStatus = exception is DiscordSocialException or InvalidOperationException ? exception.Message : "Conversation couldn't load. Use Reload to try again."; }
        finally
        {
            if (generation == _conversationGeneration) { _historyCancellation = null; _chatLoading = false; RefreshWorkspace(); }
        }
    }
    private void ChatMessageChanged(object? sender, DiscordMessageChange change)
    {
        void Update()
        {
            if (change.Message is { } incoming) { ReconcileOutgoing(incoming); NotifyIncomingMessage(incoming); }
            if (change.DeletedId > 0)
            {
                _deletedMessages.Add(change.DeletedId);
                if (_deletedMessages.Count > 2048) _deletedMessages.Remove(_deletedMessages.Min());
                var old = Messages.FirstOrDefault(message => message.Id == change.DeletedId); if (old is not null) Messages.Remove(old);
            }
            else if (change.Message is { } message && (message.LobbyId > 0
                ? _partyChat && message.LobbyId == ConversationId : !_partyChat && message.PeerId == ConversationId)) UpsertMessage(message);
            OnPropertyChanged(nameof(ChatEmptyVisibility));
        }
        if (_context is not null && SynchronizationContext.Current != _context) _context.Post(_ => Update(), null); else Update();
    }
    private void UpsertMessage(DiscordChatMessage message)
    {
        if (_deletedMessages.Contains(message.Id)) return;
        ReconcileOutgoing(message);
        var existing = Messages.Select((item, index) => (item, index)).FirstOrDefault(value => value.item.Id == message.Id);
        if (existing.item is not null) { if (existing.item != message) Messages[existing.index] = message; }
        else
        {
            var index = 0;
            while (index < Messages.Count && (Messages[index].SentAt < message.SentAt || Messages[index].SentAt == message.SentAt && Messages[index].Id < message.Id)) index++;
            Messages.Insert(index, message);
        }
        while (Messages.Count > 200) Messages.RemoveAt(0);
        OnPropertyChanged(nameof(ChatEmptyVisibility));
    }
    public void SetChatVisible(bool visible) { _chatVisible = visible; _chat?.SetShowingChat(visible && _windowActive && ConversationVisibility == Visibility.Visible); MarkCurrentConversationRead(); }
    private void ClearConversation()
    { ConversationChanging?.Invoke(this, EventArgs.Empty); var pending = _historyCancellation; _historyCancellation = null; pending?.Cancel(); _conversationGeneration++; _chatLoading = false; Messages.Clear(); _deletedMessages.Clear(); MessageDraft = ""; }
    private void GamesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshGames();
    private void RefreshGames()
    {
        var selected = SelectedGame?.AppId;
        var games = _store?.Games.Where(game => game.AppId > 0 && game.InstallState == GameInstallState.Installed && !GameClassification.IsRuntime(game.AppId, game.Name))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];
        AvailableGames.Clear(); foreach (var game in games) AvailableGames.Add(game);
        SelectedGame = games.FirstOrDefault(game => game.AppId == selected) ?? games.FirstOrDefault();
        RefreshWorkspaceCommands();
    }
    private void RefreshWorkspace()
    {
        if (SendMessageCommand is null) return;
        var partyId = Party?.Id ?? 0;
        if (_invitePartyId != partyId) { _sentPartyInvites.Clear(); _invitePartyId = partyId; }
        if (!IsConnected) { ResetOutbox(); _conversationDrafts.Clear(); ClearConversation(); _readPositions.Clear(); _selectedFriend = null; _inviteFriend = null; _partyChat = false; OnPropertyChanged(nameof(SelectedFriend)); }
        else if (_selectedFriend is { } selected && !_discord.Snapshot.Relationships.Any(friend => friend.Id == selected.Id && friend.IsFriend))
        { ClearConversation(); _selectedFriend = null; OnPropertyChanged(nameof(SelectedFriend)); }
        if (Party is null && _partyChat) { _partyChat = false; ClearConversation(); }
        if (_inviteFriend is { } target && !InviteTargets.Any(friend => friend.Id == target.Id)) { _inviteFriend = null; OnPropertyChanged(nameof(InviteFriend)); }
        var invites = _chat?.Invites ?? [];
        for (var index = PartyInvites.Count - 1; index >= 0; index--) if (!invites.Any(invite => invite.Id == PartyInvites[index].Id)) PartyInvites.RemoveAt(index);
        foreach (var invite in invites) if (!PartyInvites.Any(existing => existing.Id == invite.Id)) PartyInvites.Add(invite);
        if (_selectedFriend is { } current)
        {
            _selectedFriend = _discord.Snapshot.Relationships.FirstOrDefault(friend => friend.Id == current.Id && friend.IsFriend);
            OnPropertyChanged(nameof(SelectedFriend));
        }
        foreach (var name in new[] { nameof(HasCommunicationPermission), nameof(PartyTitle), nameof(PartyDescription), nameof(PartyMembers), nameof(PartyVisibility),
            nameof(NoPartyVisibility), nameof(PartyCode), nameof(InviteVisibility), nameof(ConversationVisibility), nameof(NoConversationVisibility), nameof(ChatEmptyVisibility),
            nameof(ChatTitle), nameof(ChatInitial), nameof(ChatAvatarUrl), nameof(ChatSubtitle), nameof(ConversationKey), nameof(InviteTargets), nameof(InvitePeople), nameof(JoinInviteLabel), nameof(CompactPartyLabel), nameof(NavigationSocialTooltip), nameof(PlayWithFriendLabel), nameof(LoadingChatVisibility), nameof(PartyArtwork), nameof(PartyGame), nameof(CanChooseGame), nameof(LaunchLabel), nameof(FriendMenuVisibility) }) OnPropertyChanged(name);
        RefreshPartyArtwork();
        _chat?.SetShowingChat(_chatVisible && _windowActive && ConversationVisibility == Visibility.Visible);
        RefreshWorkspaceCommands();
    }
    private void RefreshWorkspaceCommands()
    {
        foreach (var command in new IRelayCommand?[] { SendMessageCommand, ReloadChatCommand, CreatePartyCommand, InviteFriendCommand, InvitePersonCommand, PlayWithFriendCommand, LeavePartyCommand, LaunchGameCommand, JoinInviteCommand, OpenPartyChatCommand }) command?.NotifyCanExecuteChanged();
        OpenDiscordMessageCommand?.NotifyCanExecuteChanged();
        RetryMessageCommand?.NotifyCanExecuteChanged();
        JoinPartyCodeCommand?.NotifyCanExecuteChanged(); CopyPartyCodeCommand?.NotifyCanExecuteChanged();
    }
    private void RefreshPartyArtwork()
    {
        var game = IsConnected ? PartyGame : null;
        if (ReferenceEquals(game, _requestedArtwork)) return;
        _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose(); _artworkCancellation = null;
        _requestedArtwork = game;
        if (game is null || _socialArtwork is null || game.HeaderImage is not null) return;
        _artworkCancellation = new CancellationTokenSource();
        _ = LoadPartyArtworkAsync(game, _artworkCancellation.Token);
    }
    private async Task LoadPartyArtworkAsync(Game game, CancellationToken token)
    {
        try
        {
            await _socialArtwork!.LoadHeadersAsync([game], token);
            if (!token.IsCancellationRequested && ReferenceEquals(game, PartyGame)) OnPropertyChanged(nameof(PartyArtwork));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Artwork is optional; retain the placeholder and usable chat. */ }
    }
    private void DisposeWorkspace()
    {
        _outboxLifetime.Cancel(); _outboxLifetime.Dispose();
        _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
        _conversationGeneration++;
        var pending = _historyCancellation; _historyCancellation = null; pending?.Cancel();
        if (_chat is not null) { _chat.SetShowingChat(false); _chat.MessageChanged -= ChatMessageChanged; }
        if (_store is not null) _store.Games.CollectionChanged -= GamesChanged;
    }
}

public sealed record DiscordInvitePerson(DiscordFriend Friend, bool InParty, bool WasInvited)
{
    public string Label => InParty ? "Joined" : WasInvited ? "Invite again" : "Invite";
}
