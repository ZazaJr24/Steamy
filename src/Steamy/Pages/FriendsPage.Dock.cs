using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Steamy.Controls;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class FriendsPage
{
    public bool IsDocked { get; private set; }
    private bool _dockChat;
    public event EventHandler? CloseRequested;

    private void InitializeDock(bool docked)
    {
        IsDocked = docked;
        if (!docked)
        {
            Loaded += (_, _) => { UiThemeService.PaletteChanged += DockPaletteChanged; ApplyDockPalette(); };
            Unloaded += (_, _) => UiThemeService.PaletteChanged -= DockPaletteChanged;
            ApplyDockPalette();
            return;
        }
        SocialRoot.Margin = new Thickness(0);
        RosterTitle.Text = "Your friends"; RosterTitle.FontSize = 14;
        SocialHeader.Visibility = Visibility.Collapsed; DockHeader.Visibility = Visibility.Visible;
        SocialWorkspaceSurface.CornerRadius = new CornerRadius(0); SocialWorkspaceSurface.BorderThickness = new Thickness(0);
        if (SocialWorkspaceSurface.Child is Grid workspace) foreach (var backdrop in workspace.Children.OfType<ShellBackdropView>()) backdrop.Visibility = Visibility.Collapsed;
        RoundedClip.SetRadius(SocialWorkspaceSurface, 0);
        ConnectIntroduction.Visibility = Visibility.Collapsed;
        ConnectLayout.ColumnDefinitions[0].Width = new GridLength(0); ConnectLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        ConnectSurface.Padding = new Thickness(12); ConnectSurface.BorderThickness = new Thickness(0); ConnectSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
        SocialStatusFooter.Margin = new Thickness(14, 0, 14, 8);
        DockChatBackButton.Visibility = Visibility.Visible;
        ConversationHeader.Padding = new Thickness(8, 12, 8, 12);
        foreach (var label in ConversationIdentity.Children.OfType<TextBlock>()) if (label.FontSize > 14) label.FontSize = 14;
        PlayWithFriendButton.ClearValue(ContentControl.ContentProperty); PlayWithFriendButton.ToolTip = "Play together";
        PlayWithFriendButton.Width = 30; PlayWithFriendButton.MinHeight = 30; PlayWithFriendButton.Padding = new Thickness(5);
        StartVoiceCallButton.Width = 30; StartVoiceCallButton.MinHeight = 30; StartVoiceCallButton.Padding = new Thickness(5);
        DiscordFriendsList.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var source = args.OriginalSource as DependencyObject;
            while (source is not null && source is not ListBoxItem)
            { if (source is ButtonBase) return; source = VisualTreeHelper.GetParent(source); }
            if (source is ListBoxItem { DataContext: DiscordFriend { IsFriend: true } friend } && DataContext is DiscordFriendsViewModel model)
            { model.SelectedFriend = friend; RevealConversation(); }
        };
        DiscordFriendsList.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter && DataContext is DiscordFriendsViewModel { SelectedFriend.IsFriend: true }) { RevealConversation(); args.Handled = true; } };
        PartyChatShortcut.Click += (_, _) => RevealConversation();
        IsVisibleChanged += (_, _) => { UpdateChatExposure(); if (!IsVisible) { GifPickerPopup.IsOpen = false; CompactPartyPopup.IsOpen = false; } };
        Loaded += (_, _) => { UiThemeService.PaletteChanged += DockPaletteChanged; ApplyDockPalette(); UpdateDockLayout(); UpdateChatExposure(); };
        Unloaded += (_, _) => UiThemeService.PaletteChanged -= DockPaletteChanged;
        UpdateDockLayout(); ApplyDockPalette();
    }
    private void DockPaletteChanged(object? sender, EventArgs args) => ApplyDockPalette();
    private void ApplyDockPalette()
    {
        var background = UiThemeService.IsHighContrastActive ? SystemColors.WindowBrush : Brushes.Transparent;
        foreach (var key in new[] { "SocialBackdropBrush", "SocialChromeBrush", "SocialCanvasBrush", "SocialSidebarBrush" }) Resources[key] = background;
        Background = background;
        if (UiThemeService.IsHighContrastActive)
        {
            Resources["SocialFieldBrush"] = Resources["SocialCardBrush"] = SystemColors.WindowBrush;
            Resources["SocialOwnBubbleBrush"] = SystemColors.HighlightBrush;
            Resources["SocialOwnTextBrush"] = SystemColors.HighlightTextBrush;
        }
        if (!UiThemeService.IsHighContrastActive)
        {
            var bubble = new SolidColorBrush(UiThemeService.IsLight ? Color.FromRgb(94, 84, 153) : Color.FromRgb(69, 64, 98)); bubble.Freeze();
            Resources["SocialOwnBubbleBrush"] = bubble;
            Resources["SocialOwnTextBrush"] = Brushes.White;
            Resources["SocialFieldBrush"] = Application.Current.Resources["SocialFieldBrush"];
            Resources["SocialCardBrush"] = Application.Current.Resources["SocialCardBrush"];
        }
    }
    private void DockFriends_Click(object sender, RoutedEventArgs args) { _dockChat = false; UpdateDockLayout(); UpdateChatExposure(); }
    private void DockChats_Click(object sender, RoutedEventArgs args) => RevealConversation();
    private void DockClose_Click(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
    internal void RevealConversation()
    {
        if (!IsDocked)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && MessageComposer.IsVisible) MessageComposer.Focus(); }));
            return;
        }
        _dockChat = true; UpdateDockLayout(); UpdateChatExposure();
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && _dockChat && MessageComposer.IsVisible) MessageComposer.Focus(); }));
    }
    internal void RevealFriends()
    { if (!IsDocked) { DiscordFriendsList.Focus(); return; } _dockChat = false; UpdateDockLayout(); UpdateChatExposure(); }
    private void UpdateChatExposure()
    { if (DataContext is DiscordFriendsViewModel model) model.SetChatVisible(IsVisible && (!IsDocked || _dockChat)); }
    private void UpdateDockLayout()
    {
        if (!IsDocked) return;
        SocialWorkspace.ColumnDefinitions[0].Width = new GridLength(_dockChat ? 0 : 1, _dockChat ? GridUnitType.Pixel : GridUnitType.Star);
        SocialWorkspace.ColumnDefinitions[1].Width = new GridLength(0);
        SocialWorkspace.ColumnDefinitions[2].Width = new GridLength(_dockChat ? 1 : 0, _dockChat ? GridUnitType.Star : GridUnitType.Pixel);
        PartyColumn.Width = PartyGap.Width = new GridLength(0); PartyPane.Visibility = Visibility.Collapsed;
        ContactListSurface.Visibility = _dockChat ? Visibility.Collapsed : Visibility.Visible;
        ChatSurface.Visibility = _dockChat ? Visibility.Visible : Visibility.Collapsed;
        DockAccount.Visibility = _dockChat ? Visibility.Collapsed : (DataContext as DiscordFriendsViewModel)?.ConnectedVisibility ?? Visibility.Collapsed;
        DockFriendsTab.Tag = _dockChat ? "False" : "True"; DockChatsTab.Tag = _dockChat ? "True" : "False";
        CompactPartyButton.Visibility = Visibility.Collapsed;
        Resources["SocialMessageMaxWidth"] = Math.Max(180d, ActualWidth - 48);
        ConversationGifPicker.MaxWidth = Math.Max(280, ActualWidth - 24);
        GifPickerPopup.HorizontalOffset = -Math.Max(0, ConversationGifPicker.MaxWidth - 34);
        ConversationGifPicker.MaxHeight = Math.Max(200, ActualHeight - 170);
        CompactPartyPopup.HorizontalOffset = -260;
        CompactPartyPopup.MaxHeight = Math.Max(240, ActualHeight - 80);
    }
}
