using System.Text.Json;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class DiscordWorkspaceTests
{
    [Fact]
    public async Task SwitchingContactsDiscardsLateHistoryFromThePreviousConversation()
    {
        var service = new SocialFixture();
        var oldHistory = new TaskCompletionSource<IReadOnlyList<DiscordChatMessage>>();
        service.History = (id, _) => id == 2 ? oldHistory.Task : Task.FromResult<IReadOnlyList<DiscordChatMessage>>([Message(20, 3, "Conversation three")]);
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 3);
        oldHistory.SetResult([Message(10, 2, "Old conversation")]);
        await Task.Delay(20);
        Assert.Equal("Conversation three", Assert.Single(model.Messages).Content);
        Assert.Equal(3UL, model.SelectedFriend!.Id);
    }

    [Fact]
    public async Task HistoryCannotResurrectDeletedMessagesOrOverwriteALiveEdit()
    {
        var service = new SocialFixture();
        var history = new TaskCompletionSource<IReadOnlyList<DiscordChatMessage>>();
        service.History = (_, _) => history.Task;
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0];
        service.Notify(new(Message(10, 2, "Edited live") with { IsEdited = true }));
        service.Notify(new(null, 11));
        history.SetResult([Message(10, 2, "Original"), Message(11, 2, "Deleted")]);
        await Task.Delay(20);
        Assert.Equal("Edited live", Assert.Single(model.Messages).Content);
    }

    [Fact]
    public async Task FailedMessagesRemainExplicitlyUnconfirmedAndDoNotReportSuccess()
    {
        var service = new SocialFixture { SendError = new DiscordSocialException("Privacy settings prevented delivery") };
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0];
        model.MessageDraft = "Ready to play?";
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Empty(model.MessageDraft);
        var failed = Assert.Single(model.Messages);
        Assert.Equal(DiscordDeliveryState.Failed, failed.DeliveryState);
        Assert.False(failed.CanResend);
        Assert.Contains("Privacy", model.ChatStatus);
    }

    [Fact]
    public async Task ConfirmedSendsDeduplicateLiveEchoesAndPreserveTextTypedWhileSending()
    {
        var service = new SocialFixture();
        var sending = new TaskCompletionSource<DiscordChatMessage>(); service.SendCompletion = sending;
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "First message";
        var send = model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(DiscordDeliveryState.Sending, Assert.Single(model.Messages).DeliveryState);
        model.MessageDraft = "Next message";
        var confirmed = Message(50, 2, "First message") with { IsOwn = true, AuthorId = 1 };
        service.Notify(new(confirmed)); sending.SetResult(confirmed);
        await send;
        Assert.Single(model.Messages);
        Assert.Equal("Next message", model.MessageDraft);
    }

    [Fact]
    public void PresenceUpdatesPreserveTheSelectedConversationAndComposer()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "Draft stays here";
        service.Notify(new(Message(1, 2, "Hello")));
        service.ChangePresence(2, "Idle");
        Assert.Equal(2UL, model.SelectedFriend!.Id);
        Assert.Equal("Idle", model.SelectedFriend.Status);
        Assert.Equal("Draft stays here", model.MessageDraft);
        Assert.Single(model.Messages);
    }

    [Fact]
    public async Task PartyLaunchUsesOnlyAMatchingInstalledLocalGameAndNeverAutostartsOnJoin()
    {
        var service = new SocialFixture(); var store = new AppDataStore(); var launcher = new LaunchFixture();
        store.Games.Add(new Game { AppId = 42, Name = "My installed game", InstallState = GameInstallState.Installed });
        store.Games.Add(new Game { AppId = 99, Name = "Not installed", InstallState = GameInstallState.NotInstalled });
        using var model = new DiscordFriendsViewModel(service, new Settings(), store, launcher);
        Assert.Single(model.AvailableGames);
        await model.CreatePartyCommand.ExecuteAsync(null);
        Assert.Equal(0, launcher.Requests);
        Assert.Equal(42, service.Party!.Game.AppId);
        await model.LaunchGameCommand.ExecuteAsync(null);
        Assert.Equal(42, launcher.LastGame!.AppId);
        service.SetParty(new(77, new(99, "Remote game name"), []));
        Assert.False(model.LaunchGameCommand.CanExecute(null));
        Assert.Equal(1, launcher.Requests);
    }

    [Fact]
    public async Task DisconnectClearsChatContentAndDisablesCommunicationActions()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; service.Notify(new(Message(1, 2, "Private conversation")));
        model.MessageDraft = "Unsaved text";
        await service.DisconnectAsync();
        Assert.Empty(model.Messages); Assert.Empty(model.MessageDraft);
        Assert.Null(model.SelectedFriend); Assert.False(model.SendMessageCommand.CanExecute(null));
        Assert.DoesNotContain("Private conversation", JsonSerializer.Serialize(new Settings().Load()));
    }

    private static DiscordChatMessage Message(ulong id, ulong peer, string text) => new(id, peer, peer, 0, 1, text, DateTimeOffset.UtcNow, false, false, false);
    [Fact]
    public void SwitchingConversationsPreservesEachDraftInMemory()
    {
        using var model = new DiscordFriendsViewModel(new SocialFixture(), new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "Draft for Two";
        model.SelectedFriend = model.Friends[1]; Assert.Empty(model.MessageDraft); model.MessageDraft = "Draft for Three";
        model.SelectedFriend = model.Friends[0]; Assert.Equal("Draft for Two", model.MessageDraft);
        model.SelectedFriend = model.Friends[1]; Assert.Equal("Draft for Three", model.MessageDraft);
    }
    [Fact]
    public async Task PlayTogetherCreatesOnePartyAndInvitesTheSelectedFriendWithoutChangingTheirChat()
    {
        var service = new SocialFixture(); var store = new AppDataStore();
        store.Games.Add(new Game { AppId = 42, Name = "Installed game", InstallState = GameInstallState.Installed });
        using var model = new DiscordFriendsViewModel(service, new Settings(), store);
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "A retained draft";
        await model.PlayWithFriendCommand.ExecuteAsync(null);
        Assert.Equal(42, service.Party!.Game.AppId); Assert.Equal(2UL, service.Invited);
        Assert.Equal(2UL, model.SelectedFriend!.Id); Assert.Equal("A retained draft", model.MessageDraft);
        Assert.True(model.InvitePeople.Single(person => person.Friend.Id == 2).WasInvited);
        Assert.Equal("Invite again", model.InvitePeople.Single(person => person.Friend.Id == 2).Label);
    }
    [Fact]
    public async Task FailedPartyInviteIsNotMarkedSentAndPendingInvitesDisableOtherPartyActions()
    {
        var service = new SocialFixture(); service.SetParty(new(9, new(42, "Installed game"), []));
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0];
        service.InviteCompletion = new TaskCompletionSource();
        var pending = model.InvitePersonCommand.ExecuteAsync(model.SelectedFriend);
        Assert.True(model.IsBusy); Assert.False(model.LeavePartyCommand.CanExecute(null)); Assert.False(model.PlayWithFriendCommand.CanExecute(null));
        service.InviteCompletion.SetException(new DiscordSocialException("Delivery failed")); await pending;
        Assert.True(model.IsError); Assert.False(model.InvitePeople.Single(person => person.Friend.Id == 2).WasInvited);
        Assert.True(model.LeavePartyCommand.CanExecute(null));
    }
    [Fact]
    public async Task SwitchingPartyLeavesThenJoinsWithoutLaunchingAGame()
    {
        var service = new SocialFixture(); service.SetParty(new(9, new(42, "Old party"), []));
        var launcher = new LaunchFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), new AppDataStore(), launcher);
        Assert.Equal("Switch party", model.JoinInviteLabel);
        await model.JoinInviteCommand.ExecuteAsync(new DiscordPartyInvite(Guid.NewGuid(), 3, "Three", "A new party"));
        Assert.Equal(new[] { "leave", "join" }, service.PartyOperations);
        Assert.Equal(11UL, service.Party!.Id); Assert.Equal("Party chat", model.ChatTitle); Assert.Equal(0, launcher.Requests);
    }
    [Fact]
    public async Task PartyArtworkLoadsWithoutOpeningTheLibraryAndCancelsObsoleteSelections()
    {
        var service = new SocialFixture(); var store = new AppDataStore(); var artwork = new PendingArtwork();
        var first = new Game { AppId = 42, Name = "First game", InstallState = GameInstallState.Installed };
        var second = new Game { AppId = 43, Name = "Second game", InstallState = GameInstallState.Installed };
        store.Games.Add(first); store.Games.Add(second);
        using var model = new DiscordFriendsViewModel(service, new Settings(), store, artwork: artwork);
        Assert.Same(first, Assert.Single(artwork.Requests).Game);
        model.SelectedGame = second;
        Assert.True(artwork.Requests[0].Token.IsCancellationRequested);
        Assert.Same(second, model.PartyGame);
        service.SetParty(new(9, new(42, "Party game"), []));
        Assert.True(artwork.Requests[1].Token.IsCancellationRequested);
        Assert.Same(first, model.PartyGame);
        service.ChangePresence(2, "Idle");
        Assert.Equal(3, artwork.Requests.Count);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.True(artwork.Requests[2].Token.IsCancellationRequested);
    }
    private sealed class PendingArtwork : IArtworkService
    {
        public List<(Game Game, CancellationToken Token)> Requests = [];
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
        { Requests.AddRange(games.Select(game => (game, cancellationToken))); return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        public Task<System.Windows.Media.Imaging.BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Settings : ISettingsService
    {
        private readonly AppSettings _settings = new() { DiscordApplicationId = "42" };
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class LaunchFixture : IGameLaunchService
    {
        public int Requests; public Game? LastGame;
        public Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default) { Requests++; LastGame = game; return Task.FromResult(new GameLaunchResult(true, "Started")); }
    }
    private sealed partial class SocialFixture : IDiscordSocialService, IDiscordChatService
    {
        public bool IsAvailable => true;
        public bool HasSavedSession => Snapshot.IsConnected;
        public bool HasCommunicationPermission => Snapshot.IsConnected;
        public string AvailabilityMessage => "Ready";
        public DiscordSnapshot Snapshot { get; private set; } = new(true, new(1, "me", "Me", null),
            [new(new(2, "two", "Two", null), DiscordRelationshipKind.Friend, "Online"), new(new(3, "three", "Three", null), DiscordRelationshipKind.Friend, "Offline")]);
        public event EventHandler? Changed;
        public event EventHandler<DiscordMessageChange>? MessageChanged;
        public DiscordParty? Party { get; private set; }
        public IReadOnlyList<DiscordPartyInvite> Invites => [];
        public Func<ulong, bool, Task<IReadOnlyList<DiscordChatMessage>>> History = (_, _) => Task.FromResult<IReadOnlyList<DiscordChatMessage>>([]);
        public Exception? SendError;
        public int SendCalls;
        public TaskCompletionSource<DiscordChatMessage>? SendCompletion;
        public TaskCompletionSource? InviteCompletion;
        public ulong Invited;
        public List<string> PartyOperations = [];
        public void Notify(DiscordMessageChange change) => MessageChanged?.Invoke(this, change);
        public void ChangePresence(ulong id, string status) { Snapshot = Snapshot with { Relationships = Snapshot.Relationships.Select(friend => friend.Id == id ? friend with { Status = status } : friend).ToArray() }; Changed?.Invoke(this, EventArgs.Empty); }
        public void SetParty(DiscordParty party) { Party = party; Changed?.Invoke(this, EventArgs.Empty); }
        public Task RestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token = default) { Snapshot = DiscordSnapshot.Disconnected; Party = null; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default) => Task.CompletedTask;
        public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default) => History(peerId, party);
        public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default)
        { SendCalls++; if (SendError is not null) throw SendError; return SendCompletion?.Task ?? Task.FromResult(Message(500, peerId, content) with { IsOwn = true }); }
        public void SetShowingChat(bool showing) { }
        public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default) { Party = new(10, game, [Snapshot.Profile!]); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public async Task InviteToPartyAsync(ulong friendId, CancellationToken token = default) { if (InviteCompletion is not null) await InviteCompletion.Task; Invited = friendId; }
        public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default) { PartyOperations.Add("join"); Party = new(11, new(43, "New party"), []); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public void DismissPartyInvite(Guid inviteId) { }
        public Task LeavePartyAsync(CancellationToken token = default) { PartyOperations.Add("leave"); Party = null; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    }
}
