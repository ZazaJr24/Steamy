using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;
using Steamy.Services;

namespace Steamy;

public partial class MainWindow
{
    private NotificationCenterViewModel _notifications = null!;
    private DiscordFriendsViewModel _socialModel = null!;
    private DispatcherTimer _toastClock = null!;
    private Pages.FriendsPage? _friendsPage;
    private void InitializeNotificationWorkspace()
    {
        _notifications = App.Services.GetRequiredService<NotificationCenterViewModel>();
        if (DiscordIntegration.Enabled)
        {
            _socialModel = App.Services.GetRequiredService<DiscordFriendsViewModel>();
            VoiceCallHost.Content = new Controls.VoiceCallBar();
        }
        NotificationBell.DataContext = NotificationPanel.DataContext = NotificationToasts.DataContext = _notifications;
        _toastClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _toastClock.Tick += (_, _) => _notifications.ExpireToasts(DateTimeOffset.UtcNow);
        _notifications.Toasts.CollectionChanged += ToastsChanged;
        _notifications.Changed += NotificationStateChanged;
        _notifications.OpenRequested += OpenNotification;
        Activated += (_, _) => _socialModel?.SetWindowActive(true);
        Deactivated += (_, _) => _socialModel?.SetWindowActive(false);
        StateChanged += (_, _) => _socialModel?.SetWindowActive(IsActive && WindowState != WindowState.Minimized);
        SizeChanged += (_, _) => { NotificationCenterBorder.MaxHeight = Math.Clamp(ActualHeight - 80, 200, 540); };
        Closed += (_, _) =>
        {
            _toastClock.Stop(); NotificationsPopup.IsOpen = false;
            _notifications.Toasts.CollectionChanged -= ToastsChanged;
            _notifications.Changed -= NotificationStateChanged; _notifications.OpenRequested -= OpenNotification;
            _socialModel?.SetWindowActive(false);
            _friendsPage = null;
        };
        NotificationStateChanged(this, EventArgs.Empty);
    }
    private void ToastsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    { if (_notifications.Toasts.Count > 0) _toastClock.Start(); else _toastClock.Stop(); }
    private void Notifications_Click(object sender, RoutedEventArgs args) => NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
    private void NotificationStateChanged(object? sender, EventArgs args)
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        if (_notifications.UnreadCount == 0) { TaskbarItemInfo.Overlay = null; return; }
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(52, 120, 246)), null, new Point(8, 8), 8, 8);
            var text = new FormattedText(_notifications.UnreadCount > 9 ? "9+" : _notifications.UnreadCount.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point((16 - text.Width) / 2, (16 - text.Height) / 2));
        }
        var image = new DrawingImage(visual.Drawing); image.Freeze(); TaskbarItemInfo.Overlay = image;
    }
    private void OpenNotification(object? sender, SteamyNotification notification)
    {
        NotificationsPopup.IsOpen = false;
        if (notification.Content.Kind == SteamyNotificationKind.Download) { RootNavigation.Navigate(typeof(Pages.DownloadsPage)); return; }
        if (notification.Content.Kind == SteamyNotificationKind.Information) return;
        if (!DiscordIntegration.Enabled) return;
        OpenSocialDock();
        if (notification.Content.Kind == SteamyNotificationKind.FriendRequest) { _socialModel.OpenFriendRequests(); _friendsPage?.RevealFriends(); }
        else if (notification.Content.Kind == SteamyNotificationKind.PartyInvite) _friendsPage?.RevealParty();
        else if (notification.Content.LobbyId > 0)
        { if (_socialModel.Party?.Id == notification.Content.LobbyId) { _socialModel.OpenPartyChatCommand.Execute(null); _friendsPage?.RevealConversation(); } }
        else
        {
            _socialModel.SelectedFilter = "Friends"; _socialModel.SearchText = "";
            var friend = _socialModel.Friends.FirstOrDefault(person => person.Id == notification.Content.PeerId);
            if (friend is not null) _socialModel.SelectedFriend = friend;
            _friendsPage?.RevealConversation();
        }
    }
}
