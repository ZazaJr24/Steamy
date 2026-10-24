using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDiscordSdkRuntime()
    {
        Assert.True(DiscordConfiguration.SdkInstalled);
        using var client = new DiscordSdkClient(DiscordConfiguration.SdkDirectory);
        Assert.False(client.Snapshot.IsConnected);
        var nativeClient = typeof(DiscordSdkClient).GetField("_client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var type = nativeClient.GetType();
        type.GetMethod("SetApplicationId")!.Invoke(nativeClient, [ulong.Parse(DiscordConfiguration.DefaultApplicationId)]);
        var scopes = (string)type.GetMethod("GetDefaultCommunicationScopes")!.Invoke(null, null)!;
        Assert.Contains("sdk.social_layer", scopes);
        using var verifier = (IDisposable)type.GetMethod("CreateAuthorizationCodeVerifier")!.Invoke(nativeClient, null)!;
        var verifierText = (string)verifier.GetType().GetMethod("Verifier")!.Invoke(verifier, null)!;
        Assert.InRange(verifierText.Length, 43, 128);
        using var challenge = (IDisposable)verifier.GetType().GetMethod("Challenge")!.Invoke(verifier, null)!;
        var challengeText = (string)challenge.GetType().GetMethod("Challenge")!.Invoke(challenge, null)!;
        var expectedChallenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(verifierText))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expectedChallenge, challengeText);
        // Native invite updates refer to the same message; show one invitation and
        // never accept invitations belonging to a different Discord application.
        typeof(DiscordSdkClient).GetField("_applicationId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(client, 42UL);
        var receive = typeof(DiscordSdkClient).GetMethod("ReceiveInvite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var assembly = type.Assembly;
        IDisposable Invite(ulong application)
        {
            var value = (IDisposable)Activator.CreateInstance(assembly.GetType("Discord.Sdk.ActivityInvite")!)!;
            value.GetType().GetMethod("SetApplicationId")!.Invoke(value, [application]);
            value.GetType().GetMethod("SetMessageId")!.Invoke(value, [99UL]);
            value.GetType().GetMethod("SetSenderId")!.Invoke(value, [7UL]);
            value.GetType().GetMethod("SetType")!.Invoke(value, [Enum.Parse(assembly.GetType("Discord.Sdk.ActivityActionTypes")!, "Join")]);
            return value;
        }
        using var initialInvite = Invite(42); receive.Invoke(client, [initialInvite]);
        var inviteId = Assert.Single(client.Invites).Id;
        using var updatedInvite = Invite(42); receive.Invoke(client, [updatedInvite]);
        Assert.Equal(inviteId, Assert.Single(client.Invites).Id);
        using var foreignInvite = Invite(43); receive.Invoke(client, [foreignInvite]);
        Assert.Equal(inviteId, Assert.Single(client.Invites).Id);
        client.SetShowingChat(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var unavailableHistory = client.GetMessagesAsync(1, false, deadline.Token);
            // This exact transition crashed the app: native code still owns the free
            // function pointer after the managed call returns and the GC runs.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            PumpUntil(() => unavailableHistory.IsCompleted);
            Assert.Throws<DiscordSocialException>(() => unavailableHistory.GetAwaiter().GetResult());
        }
        // Exercise real native initialization, registered callbacks, pumping and disposal
        // without requesting authorization or accessing a user's Discord account.
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        GC.KeepAlive(challenge);
    }

    private sealed class DiscordOfflineFixture : IDiscordSocialService
    {
        public bool IsAvailable => false;
        public bool HasSavedSession => false;
        public string AvailabilityMessage => "Discord is optional and not configured in this test.";
        public DiscordSnapshot Snapshot => DiscordSnapshot.Disconnected;
        public event EventHandler? Changed { add { } remove { } }
        public Task RestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken token = default) => throw new InvalidOperationException("No live Discord in UI tests.");
        public Task DisconnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default) => throw new InvalidOperationException("No live Discord in UI tests.");
        public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default) => throw new InvalidOperationException("No live Discord in UI tests.");
    }
    private static void CheckHostedDiscordSdkRuntime()
    {
        using var client = new HostedDiscordSdkClient(DiscordConfiguration.SdkDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var history = client.GetMessagesAsync(1, false, deadline.Token);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        PumpUntil(() => history.IsCompleted);
        Assert.Throws<DiscordSocialException>(() => history.GetAwaiter().GetResult());
        Assert.False(client.Snapshot.IsConnected);
    }

    private static void CheckNativeFriendsNavigation(IServiceProvider provider)
    {
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { Width = 1280, Height = 800, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(150));
            var navigation = (Grid)window.FindName("ShellPillItems");
            Assert.Equal(8, navigation.ColumnDefinitions.Count);
            Assert.Empty(navigation.RowDefinitions);
            var friends = (Button)window.FindName("ShellFriendsButton");
            Assert.Equal(typeof(FriendsPage), friends.Tag);
            friends.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<FriendsPage>(window).Any());
            Assert.Equal("Page", window.RootNavigationView.Tag);
            Assert.True(window.IsSocialDockOpen);
            Assert.Same(provider.GetRequiredService<DiscordFriendsViewModel>(), Descendants<FriendsPage>(window).Single().DataContext);
            var model = provider.GetRequiredService<DiscordFriendsViewModel>();
            model.SelectedFriend = model.Friends.First(friend => friend.IsFriend);
            for (var i = 0; i < 150; i++) model.Messages.Add(new DiscordChatMessage((ulong)(5000 + i), 1, 1, 0, 8, "Navigation history " + i, DateTimeOffset.UtcNow.AddDays(-(149 - i) / 50), false, false, false));
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            var page = Descendants<FriendsPage>(window).Single();
            page.RevealConversation();
            Assert.False(ScrollViewer.GetCanContentScroll(page), "Friends must opt out of the NavigationView's outer infinite-height scroll host.");
            foreach (var size in new[] { new Size(940, 640), new Size(1440, 900) })
            {
                window.WindowState = WindowState.Normal; window.Width = size.Width; window.Height = size.Height;
                window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(200));
                var toolbar = (FrameworkElement)window.FindName("PersistentNavigation");
                Assert.InRange(toolbar.TranslatePoint(new Point(), window).Y, 0, 20);
                Assert.InRange(page.ActualWidth, window.ActualWidth - 2, window.ActualWidth);
                Assert.Null(window.FindName("SocialDockHost"));
                var composer = (FrameworkElement)page.FindName("MessageComposer");
                var composerBounds = composer.TransformToAncestor(window).TransformBounds(new Rect(composer.RenderSize));
                Assert.True(composerBounds.Bottom < window.ActualHeight - 10, "The actual navigation host must keep the composer on screen with long history.");
                var heading = (FrameworkElement)page.FindName("SocialHeader");
                Assert.InRange(heading.TranslatePoint(new Point(), window).Y, 48, 120);
                var list = (ListBox)page.FindName("ChatMessages");
                var scroll = Descendants<ScrollViewer>(list).First();
                Assert.InRange(scroll.ViewportHeight, 100, window.ActualHeight - 150);
                Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset < 10, "Following the latest message must survive a window resize.");
                Assert.True(Descendants<ListBoxItem>(list).Count() < 80);
                Assert.All(Descendants<ScrollViewer>(page).Where(viewer => viewer.ActualWidth > 0), viewer => Assert.True(viewer.ScrollableWidth < 1));
            }
            CaptureSocialWindow(window, "social-navigation-long-history");
            CheckNotificationNavigation(window, provider);
            ((Button)window.FindName("ShellSettingsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<SettingsPage>(window).Any());
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
        var launchedFromInvite = new MainWindow(openSocialOnStartup: true) { Width = 1280, Height = 800, ShowInTaskbar = false };
        try
        {
            Application.Current.MainWindow = launchedFromInvite; launchedFromInvite.Show();
            PumpUntil(() => Descendants<FriendsPage>(launchedFromInvite).Any());
            Assert.True(launchedFromInvite.IsSocialDockOpen);
            Assert.DoesNotContain(Descendants<DashboardPage>(launchedFromInvite), dashboard => dashboard.IsVisible);
        }
        finally { launchedFromInvite.Close(); Application.Current.MainWindow = previous; }
    }
    private static void CheckNotificationNavigation(MainWindow window, IServiceProvider provider)
    {
        var model = provider.GetRequiredService<DiscordFriendsViewModel>();
        var center = provider.GetRequiredService<NotificationCenterViewModel>();
        var social = (DiscordUiFixture)provider.GetRequiredService<IDiscordSocialService>();
        center.ClearCommand.Execute(null);
        social.Emit(new DiscordChatMessage(88001, 2, 2, 0, 8, "Ready for a game?", DateTimeOffset.UtcNow, false, false, false, "Mika"));
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        Assert.Equal(1, center.UnreadCount); Assert.Single(center.Toasts); Assert.NotNull(window.TaskbarItemInfo.Overlay);
        Assert.Equal(1, model.UnreadMessages(2));
        var message = Assert.Single(center.Items);
        center.OpenCommand.Execute(message); PumpDispatcher(TimeSpan.FromMilliseconds(120));
        Assert.Equal(2UL, model.SelectedFriend!.Id); Assert.Equal(0, center.UnreadCount); Assert.Empty(center.Toasts);
        social.AddInvite(new DiscordPartyInvite(Guid.NewGuid(), 1, "Kai", "Invited you to tonight's session"));
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        Assert.Equal(1, center.UnreadCount); Assert.Single(center.Toasts);
        CaptureSocialWindow(window, "social-notification-toast");
        ((Button)window.FindName("NotificationBell")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
        Assert.True(((System.Windows.Controls.Primitives.Popup)window.FindName("NotificationsPopup")).IsOpen);
        var panel = (NotificationCenterPanel)window.FindName("NotificationPanel");
        Assert.Same(center, panel.DataContext);
        if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is { } directory)
        {
            var size = panel.RenderSize; var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.Resources["SocialNotificationBrush"], null, new Rect(size));
            bitmap.Render(background); bitmap.Render(panel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "notifications-center-dark.png")); encoder.Save(stream);
        }
        ((System.Windows.Controls.Primitives.Popup)window.FindName("NotificationsPopup")).IsOpen = false;
        center.MarkAllReadCommand.Execute(null);
    }

    private static void CaptureSocialWindow(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is not { } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }

    private static void CheckDiscordFriends()
    {
        CheckChatScrollBehavior();
        var service = new DiscordUiFixture();
        var settings = new FirstRunMemorySettings();
        var store = new AppDataStore();
        store.Games.Add(new Game { AppId = 1091500, Name = "Cyberpunk 2077", InstallState = GameInstallState.Installed, HeaderImage = FixtureArtwork.Read(1091500, "header") });
        using var model = new DiscordFriendsViewModel(service, settings, store);
        model.SelectedFriend = model.Friends.First(friend => friend.Username == "kai");
        var page = new FriendsPage(model);
        var previousMainWindow = Application.Current.MainWindow;
        using var host = new DiscordRenderHost(page, previousMainWindow);
        var artifacts = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            UiThemeService.Apply(theme);
            UiThemeService.ApplyGlassPolicy(highContrast: false);
            Assert.Equal(theme == "Light", UiThemeService.IsLight);
            RenderDiscord(page, "friends-" + theme.ToLowerInvariant(), artifacts);
            Assert.Equal(2, model.Friends.Count);
            Assert.Contains("1 online", model.FriendSummary);
            var card = Descendants<DiscordConnectionCard>(page).Single();
            Assert.Same(model, card.Model);
            Assert.Equal(Visibility.Visible, model.DisconnectVisibility);
            foreach (var width in new[] { 860d, 1200d })
            {
                var size = new Size(width, 800);
                page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
                var field = (FrameworkElement)page.FindName("MessageComposer");
                var send = (FrameworkElement)page.FindName("SendMessageButton");
                Assert.True(field.ActualWidth >= 200, "Chat needs a usable composer on compact windows.");
                var fieldBounds = field.TransformToAncestor(page).TransformBounds(new Rect(field.RenderSize));
                var sendBounds = send.TransformToAncestor(page).TransformBounds(new Rect(send.RenderSize));
                Assert.True(fieldBounds.Right <= sendBounds.Left);
                Assert.True(sendBounds.Right <= width, "Send request must remain within the page.");
                Assert.All(Descendants<ScrollViewer>(page).Where(scroll => scroll.ActualWidth > 0), scroll => Assert.True(scroll.ScrollableWidth < 1, "Friends/chat must fit without horizontal scrolling."));
            }
        }
        model.MessageDraft = "Ready when you are!";
        model.SendMessageCommand.Execute(null);
        Assert.Empty(model.MessageDraft);
        Assert.Contains(model.Messages, message => message.Content == "Ready when you are!");
        model.CreatePartyCommand.Execute(null);
        Assert.NotNull(service.Party);
        RenderDiscord(page, "friends-party", artifacts);
        model.SelectedFriend = model.Friends.First(friend => friend.Username == "kai");
        model.InviteFriendCommand.Execute(null);
        Assert.Equal(1UL, service.Invited);
        var incoming = new DiscordPartyInvite(Guid.NewGuid(), 1, "Kai", "Invited you to a Cyberpunk 2077 session");
        service.AddInvite(incoming);
        Assert.Single(model.PartyInvites); Assert.Equal("Switch party", model.JoinInviteLabel);
        UiThemeService.Apply("Dark"); RenderDiscord(page, "friends-invitation", artifacts);
        var join = Descendants<Wpf.Ui.Controls.Button>(page).Single(button => Equals(button.Content, "Switch party"));
        Assert.Same(model.JoinInviteCommand, join.Command); Assert.Same(incoming, join.CommandParameter);
        var joined = model.JoinInviteCommand.ExecuteAsync(join.CommandParameter);
        PumpUntil(() => joined.IsCompleted); joined.GetAwaiter().GetResult();
        Assert.Equal(901UL, service.Party!.Id); Assert.Empty(model.PartyInvites);
        RenderDiscord(page, "friends-joined-party", artifacts);
        model.LeavePartyCommand.Execute(null);
        model.SelectedFilter = "Requests";
        Assert.Equal(2, model.Friends.Count);
        Assert.Contains(model.Friends, friend => friend.IsIncoming);
        Assert.Contains(model.Friends, friend => friend.IsOutgoing);
        UiThemeService.Apply("Dark");
        RenderDiscord(page, "friends-requests", artifacts);
        model.SearchText = "LUNA";
        Assert.Single(model.Friends);
        Assert.Equal("luna", model.Friends[0].Username);
        model.SearchText = "";
        model.AcceptCommand.Execute(model.Friends.First(friend => friend.IsIncoming));
        Assert.Equal(1, service.Accepted);
        Assert.Single(model.Friends);
        model.SelectedFilter = "Friends";
        Assert.Equal(3, model.Friends.Count);
        model.NewFriend = "new.teammate";
        model.SendRequestCommand.Execute(null);
        Assert.Equal("new.teammate", service.SentTo);
        Assert.Empty(model.NewFriend);

        model.DisconnectCommand.Execute(null);
        Assert.False(model.IsConnected);
        Assert.Empty(model.Friends);
        Assert.False(model.SendRequestCommand.CanExecute(null));
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        service.Available = false;
        model.RefreshCommand.Execute(null);
        Assert.False(model.ConnectCommand.CanExecute(null));
        RenderDiscord(page, "friends-unconfigured", artifacts);

        var setup = new FirstRunSetup(settings, new ReadyDownloaderFixture());
        ClickButton(setup, "NextButton"); ClickButton(setup, "NextButton"); ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, ((StackPanel)setup.FindName("DiscordStep")).Visibility);
        ((DiscordConnectionCard)setup.FindName("SetupDiscordConnection")).Model = model;
        RenderDiscord(setup, "setup-discord", artifacts);
        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, ((StackPanel)setup.FindName("ControllerStep")).Visibility);
        RenderDiscord(setup, "setup-controllers", artifacts);
        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, ((StackPanel)setup.FindName("ReadyStep")).Visibility);
        ClickButton(setup, "NextButton");
        Assert.True(settings.Load().FirstRunCompleted);
        Assert.Equal(1, settings.SaveCount);
        App.ApplyCulture("System Default");
        UiThemeService.Apply("Dark");
    }

    private static void CheckChatScrollBehavior()
    {
        var fixture = new DiscordUiFixture();
        using var model = new DiscordFriendsViewModel(fixture, new FirstRunMemorySettings());
        model.SelectedFriend = model.Friends[0];
        var page = new FriendsPage(model);
        var window = new Window { Content = page, Width = 1200, Height = 800, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(150));
            for (var i = 0; i < 2000; i++) model.Messages.Add(new DiscordChatMessage((ulong)(3000 + i), 1, 1, 0, 8, "Message " + i, DateTimeOffset.UtcNow, false, false, false));
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            var messages = (ListBox)page.FindName("ChatMessages");
            var scroll = Descendants<ScrollViewer>(messages).First();
            Assert.True(model.Messages.Count >= 2000, "The virtualization stress must keep the complete synthetic history in the list.");
            Assert.True(scroll.ScrollableHeight > 500);
            Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset < 5, "New conversations follow their newest message.");
            Assert.True(Descendants<ListBoxItem>(messages).Count() < 80, "Offscreen messages must remain virtualized.");
            Assert.All(Descendants<ChatImagePreview>(messages), preview => Assert.Equal(Visibility.Collapsed, preview.Visibility));
            scroll.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Home) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            scroll.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Home) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent });
            scroll.ScrollToTop(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
            model.Messages.Add(new DiscordChatMessage(7000, 1, 1, 0, 8, "A new incoming message", DateTimeOffset.UtcNow, false, false, false));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.True(scroll.VerticalOffset < 5, "Incoming messages must not interrupt reading older history.");
            model.RefreshCommand.Execute(null); PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.True(scroll.VerticalOffset < 5, "Presence and friend refreshes must preserve the current reading position.");
            // Live/history ingestion deliberately retains 200 messages. Keep the
            // anchor phase within that production window so an evicted anchor
            // cannot turn this into an accidental fallback-to-top assertion.
            while (model.Messages.Count > 200) model.Messages.RemoveAt(0);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            scroll.ScrollToTop(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
            // Persist the same history on the fixture so returning to it can restore a real message anchor.
            var originalFriend = model.SelectedFriend!;
            IReadOnlyList<DiscordChatMessage> history = model.Messages.ToArray();
            fixture.HistoryOverride = (id, _) => id == originalFriend.Id ? history : [];
            model.SelectedFriend = model.Friends.First(friend => friend.Id != originalFriend.Id);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            model.SelectedFriend = model.Friends.First(friend => friend.Id == originalFriend.Id);
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.True(scroll.VerticalOffset < 10, "Returning to a conversation must restore the old reading position.");
            scroll.ScrollToVerticalOffset(Math.Min(850, scroll.ScrollableHeight / 2)); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var middleOffset = scroll.VerticalOffset;
            model.SelectedFriend = model.Friends.First(friend => friend.Id != originalFriend.Id); PumpDispatcher(TimeSpan.FromMilliseconds(60));
            model.SelectedFriend = model.Friends.First(friend => friend.Id == originalFriend.Id); PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.InRange(scroll.VerticalOffset, middleOffset - 50, middleOffset + 50);
            window.Content = null; PumpDispatcher(TimeSpan.FromMilliseconds(40)); window.Content = page; PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.InRange(scroll.VerticalOffset, middleOffset - 50, middleOffset + 50);
            var latest = (Wpf.Ui.Controls.Button)page.FindName("JumpLatestButton");
            Assert.Equal(Visibility.Visible, latest.Visibility);
            latest.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset < 5);
        }
        finally { window.Close(); }
    }

    private sealed class DiscordRenderHost : IDisposable
    {
        private readonly Window _window;
        private readonly Window? _previous;
        public DiscordRenderHost(Page page, Window? previous) { _previous = previous; _window = new Window { Content = page, Width = 1200, Height = 800 }; }
        public void Dispose() { _window.Content = null; _window.Close(); Application.Current.MainWindow = _previous; }
    }

    private static void RenderDiscord(FrameworkElement view, string name, string? directory)
    {
        var size = new Size(1200, 800);
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.Resources["WorkspaceBackgroundBrush"], null, new Rect(size));
        bitmap.Render(backdrop);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }

    private sealed partial class DiscordUiFixture : IDiscordSocialService, IDiscordChatService
    {
        private static DiscordFriend Friend(ulong id, string name, DiscordRelationshipKind kind, string status = "Offline") => new(new(id, name, name == "luna" ? "Luna" : name == "kai" ? "Kai" : "Mika", null), kind, status);
        public bool Available = true;
        public bool IsAvailable => Available;
        public bool HasSavedSession => Snapshot.IsConnected;
        public string AvailabilityMessage => Available ? "Connected securely with Discord." : "Discord support isn't installed in this build yet. You can connect later.";
        public DiscordSnapshot Snapshot { get; private set; } = new(true, new(100, "zaza", "Zaza", null),
            [Friend(1, "kai", DiscordRelationshipKind.Friend, "Online"), Friend(2, "mika", DiscordRelationshipKind.Friend),
                Friend(3, "luna", DiscordRelationshipKind.PendingIncoming), Friend(4, "waiting", DiscordRelationshipKind.PendingOutgoing)]);
        public int Accepted;
        public string? SentTo;
        public ulong Invited;
        public bool HasCommunicationPermission => Snapshot.IsConnected;
        public DiscordParty? Party { get; private set; }
        private readonly List<DiscordPartyInvite> _invitations = [];
        public IReadOnlyList<DiscordPartyInvite> Invites => _invitations;
        public void AddInvite(DiscordPartyInvite invite) { _invitations.Add(invite); Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler<DiscordMessageChange>? MessageChanged;
        public void Emit(DiscordChatMessage message) => MessageChanged?.Invoke(this, new(message));
        public event EventHandler? Changed;
        public Func<ulong, bool, IReadOnlyList<DiscordChatMessage>>? HistoryOverride;
        public Task RestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token = default) { SetVoice(DiscordVoiceSnapshot.Empty); Snapshot = DiscordSnapshot.Disconnected; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default) { SentTo = usernameOrId; return Task.CompletedTask; }
        public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default)
        { Accepted++; Snapshot = Snapshot with { Relationships = Snapshot.Relationships.Select(f => f.Id == friend.Id ? f with { Kind = DiscordRelationshipKind.Friend } : f).ToArray() }; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default)
        {
            if (HistoryOverride is not null) return Task.FromResult(HistoryOverride(peerId, party));
            IReadOnlyList<DiscordChatMessage> messages = [
                new(1001, peerId, peerId, party ? peerId : 0, 8, "Are you around for a game tonight?", DateTimeOffset.UtcNow.AddMinutes(-4), false, false, false),
                new(1002, 100, peerId, party ? peerId : 0, 8, "Absolutely. Let's get everyone together.", DateTimeOffset.UtcNow.AddMinutes(-3), true, false, false),
                new(1003, peerId, peerId, party ? peerId : 0, 8, "Nice! Send me a party invite when you're ready.", DateTimeOffset.UtcNow.AddMinutes(-1), false, false, false) ];
            return Task.FromResult(messages);
        }
        public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default)
        { var message = new DiscordChatMessage(2000, 100, peerId, party ? peerId : 0, 8, content, DateTimeOffset.UtcNow, true, false, false); MessageChanged?.Invoke(this, new(message)); return Task.FromResult(message); }
        public void SetShowingChat(bool showing) { }
        public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default)
        { Party = new(900, game, [Snapshot.Profile!]); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task InviteToPartyAsync(ulong friendId, CancellationToken token = default) { Invited = friendId; return Task.CompletedTask; }
        public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default)
        { _invitations.RemoveAll(invite => invite.Id == inviteId); Party = new(901, new(1091500, "Cyberpunk 2077"), [Snapshot.Profile!, Snapshot.Relationships.First(friend => friend.Id == 1).Profile]); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public void DismissPartyInvite(Guid inviteId) { _invitations.RemoveAll(invite => invite.Id == inviteId); Changed?.Invoke(this, EventArgs.Empty); }
        public Task LeavePartyAsync(CancellationToken token = default) { Party = null; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    }
}
