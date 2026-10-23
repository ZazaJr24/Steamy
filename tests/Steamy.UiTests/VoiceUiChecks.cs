using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckVoiceUi(IServiceProvider provider)
    {
        var voice = provider.GetRequiredService<DiscordVoiceViewModel>();
        var social = provider.GetRequiredService<IDiscordSocialService>();
        Assert.False(voice.Active);
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { Width = 980, Height = 620, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var start = voice.StartCallCommand.ExecuteAsync(social.Snapshot.Relationships.First(friend => friend.IsFriend));
            PumpUntil(() => start.IsCompleted); start.GetAwaiter().GetResult(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
            var bar = Descendants<VoiceCallBar>(window).Single(); Assert.True(bar.IsVisible);
            foreach (var page in new[] { typeof(DownloadsPage), typeof(FriendsPage), typeof(ControllersPage) })
            {
                if (page == typeof(FriendsPage)) window.OpenSocialDock(); else window.RootNavigationView.Navigate(page);
                PumpDispatcher(TimeSpan.FromMilliseconds(100));
                Assert.True(voice.Active); Assert.True(bar.IsVisible);
                Assert.True(bar.TranslatePoint(new Point(0, bar.ActualHeight), window).Y <= window.ActualHeight + 1);
            }
            window.OpenSocialDock(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var friends = Descendants<FriendsPage>(window).Single();
            var workspace = provider.GetRequiredService<DiscordFriendsViewModel>(); workspace.SelectedFriend = workspace.Friends.First(friend => friend.IsFriend); workspace.MessageDraft = "";
            friends.RevealConversation(); PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var composer = (System.Windows.Controls.TextBox)friends.FindName("MessageComposer"); composer.Focus();
            using (var navigation = new GamepadNavigation(window, provider.GetRequiredService<IControllerBackend>(), provider.GetRequiredService<ISettingsService>(), pollHardware: false))
            {
                navigation.Perform(GamepadAction.Keyboard); PumpDispatcher(TimeSpan.FromMilliseconds(60));
                var key = Descendants<System.Windows.Controls.Button>(window).Single(button => Equals(button.Content, "q"));
                key.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal("q", workspace.MessageDraft);
                navigation.Perform(GamepadAction.Back); PumpDispatcher(TimeSpan.FromMilliseconds(50));
                Assert.DoesNotContain(Descendants<System.Windows.Controls.Button>(window), button => Equals(button.Content, "q"));
                TracePhase($"Voice controller navigation: active={window.IsActive}, dockFocus={friends.IsKeyboardFocusWithin}, focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                navigation.Perform(GamepadAction.NextPage);
                // Navigation is dispatched and transitions asynchronously; a fixed
                // 100-ms pause can expire before a correctly requested page appears.
                PumpUntil(() => Descendants<ControllersPage>(window).Any(page => page.IsVisible));
                Assert.Single(Descendants<ControllersPage>(window)); Assert.True(voice.Active);
            }
            voice.MuteCommand.Execute(null); Assert.True(voice.Muted);
            var leave = voice.LeaveCommand.ExecuteAsync(null); PumpUntil(() => leave.IsCompleted); leave.GetAwaiter().GetResult();
            PumpDispatcher(TimeSpan.FromMilliseconds(80)); Assert.False(bar.IsVisible);
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
    }
    private sealed partial class DiscordUiFixture : IDiscordVoiceService
    {
        public DiscordVoiceSnapshot Voice { get; private set; } = DiscordVoiceSnapshot.Empty;
        public event EventHandler? VoiceChanged;
        private void SetVoice(DiscordVoiceSnapshot value) { Voice = value; VoiceChanged?.Invoke(this, EventArgs.Empty); }
        public Task JoinPartyVoiceAsync(CancellationToken token = default) { SetVoice(new(Party!.Id, "Party voice", false, "Connected", false, false, false, [])); return Task.CompletedTask; }
        public Task StartPrivateCallAsync(ulong friend, CancellationToken token = default) { SetVoice(new(777, "Call with Kai", true, "Connected", false, false, false, [new(Snapshot.Profile!, false, false, 100), new(Snapshot.Relationships[0].Profile, true, false, 100)])); return Task.CompletedTask; }
        public Task AcceptVoiceInviteAsync(DiscordVoiceInvite invite, CancellationToken token = default) => StartPrivateCallAsync(invite.HostId, token);
        public Task LeaveVoiceAsync(CancellationToken token = default) { SetVoice(DiscordVoiceSnapshot.Empty); return Task.CompletedTask; }
        public Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default) => Task.FromResult(new DiscordAudioDevices([], []));
        public Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default) => Task.CompletedTask;
        public Task SetParticipantVolumeAsync(ulong user, float volume, CancellationToken token = default) => Task.CompletedTask;
    }
}
