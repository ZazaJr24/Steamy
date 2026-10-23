using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Globalization;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using Steamy.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;
using Steamy.Services;

namespace Steamy.Pages;

public partial class FriendsPage : Page
{
    private ScrollViewer? _messageScroll;
    private bool _followLatest = true;
    private bool _tailQueued;
    private string? _conversationKey;
    private bool _scrollingToLatest;
    private bool _userScrolling;
    private bool _restorePending, _restoreQueued, _restoring;
    private int _restoreGeneration;
    public FriendsPage() : this(App.Services.GetRequiredService<DiscordFriendsViewModel>()) { }
    internal FriendsPage(DiscordFriendsViewModel model, bool docked = false)
    {
        InitializeComponent(); DataContext = model;
        InitializeDock(docked);
        SizeChanged += (_, _) =>
        {
            if (IsDocked) { UpdateDockLayout(); return; }
            var contacts = Math.Clamp(ActualWidth * .25, 240, 300);
            SocialWorkspace.ColumnDefinitions[0].Width = new GridLength(contacts);
            PartyColumn.Width = PartyGap.Width = new GridLength(0);
            PartyPane.Visibility = Visibility.Collapsed;
            CompactPartyButton.Visibility = Visibility.Visible;
            Resources["SocialMessageMaxWidth"] = Math.Clamp((ActualWidth - contacts - 80) * .72, 260, 600);
        };
        Loaded += (_, _) =>
        {
            UpdateChatExposure(); model.Messages.CollectionChanged += MessagesChanged; model.PropertyChanged += ConversationChanged; model.ConversationChanging += BeforeConversationChanges;
            ((GifPickerViewModel)ConversationGifPicker.DataContext).Picked += GifPicked;
            ChatMessages.ApplyTemplate(); _messageScroll = FindScroll(ChatMessages);
            if (_messageScroll is not null)
            {
                SmoothScroll.SetEnabled(_messageScroll, true); _messageScroll.ScrollChanged += ChatScrolled;
                _messageScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UserWheel), true);
                _messageScroll.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UserScrollKey), true);
                _messageScroll.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UserScrollbar), true);
            }
            _conversationKey = model.ConversationKey; PrepareReadPosition(model);
        };
        Unloaded += (_, _) =>
        {
            SaveReadPosition(model); _restoreGeneration++; _restorePending = false;
            model.SetChatVisible(false); model.Messages.CollectionChanged -= MessagesChanged; model.PropertyChanged -= ConversationChanged; model.ConversationChanging -= BeforeConversationChanges;
            ((GifPickerViewModel)ConversationGifPicker.DataContext).Picked -= GifPicked;
            GifPickerPopup.IsOpen = false;
            if (_messageScroll is not null)
            {
                _messageScroll.ScrollChanged -= ChatScrolled;
                _messageScroll.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UserWheel));
                _messageScroll.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UserScrollKey));
                _messageScroll.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UserScrollbar));
            }
            _messageScroll = null; CompactPartyPopup.IsOpen = false;
        };
    }
    private void MessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { if (_restorePending) QueueReadPosition(); else if (_followLatest) QueueLatest(); else JumpLatestButton.Visibility = Visibility.Visible; }
    private void ConversationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not DiscordFriendsViewModel model) return;
        if (IsDocked && args.PropertyName == nameof(model.ConnectedVisibility)) UpdateDockLayout();
        if (args.PropertyName == nameof(model.ConversationKey) && model.ConversationKey != _conversationKey)
        { _conversationKey = model.ConversationKey; PrepareReadPosition(model); }
        else if (args.PropertyName == nameof(model.LoadingChatVisibility) && _restorePending) QueueReadPosition();
    }
    private void BeforeConversationChanges(object? sender, EventArgs args)
    {
        if (sender is not DiscordFriendsViewModel model || _restoring || _restorePending) return;
        SaveReadPosition(model); _restorePending = true; _restoreGeneration++;
    }
    private void SaveReadPosition(DiscordFriendsViewModel model)
    {
        if (_messageScroll is null || _conversationKey is null || _restorePending || _restoring) return;
        var anchor = VisibleMessages(ChatMessages).Select(item => (Item: item, Top: item.TranslatePoint(new Point(), _messageScroll).Y))
            .Where(item => item.Item.ActualHeight > 0 && item.Top + item.Item.ActualHeight > 1 && item.Top < _messageScroll.ViewportHeight)
            .OrderBy(item => item.Top).FirstOrDefault();
        model.RememberReadPosition(_conversationKey, new(_followLatest, _messageScroll.VerticalOffset,
            (anchor.Item?.DataContext as DiscordChatMessage)?.Id ?? 0, anchor.Top));
    }
    private void PrepareReadPosition(DiscordFriendsViewModel model)
    {
        _restoreGeneration++; _restorePending = true; _userScrolling = false;
        _followLatest = model.ReadPosition(model.ConversationKey)?.FollowLatest ?? true; QueueReadPosition();
    }
    private void QueueReadPosition()
    {
        if (_restoreQueued || !_restorePending) return; _restoreQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreQueued = false;
            if (!_restorePending || _messageScroll is null || DataContext is not DiscordFriendsViewModel model || model.LoadingChatVisibility == Visibility.Visible) return;
            var position = model.ReadPosition(model.ConversationKey);
            if (position is null || position.FollowLatest) { _restorePending = false; _followLatest = true; QueueLatest(); return; }
            var generation = _restoreGeneration; _restoring = true; _followLatest = false;
            var anchor = model.Messages.FirstOrDefault(message => message.Id == position.AnchorId);
            if (anchor is not null) { ChatMessages.ScrollIntoView(anchor); ChatMessages.UpdateLayout(); }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation != _restoreGeneration || _messageScroll is null) { _restoring = false; return; }
                var item = VisibleMessages(ChatMessages).FirstOrDefault(item => item.DataContext is DiscordChatMessage message && message.Id == position.AnchorId);
                var offset = item is null ? position.Offset : _messageScroll.VerticalOffset + item.TranslatePoint(new Point(), _messageScroll).Y - position.AnchorTop;
                _messageScroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, _messageScroll.ScrollableHeight));
                _restorePending = false; _restoring = false; JumpLatestButton.Visibility = Visibility.Visible;
            }));
        }));
    }
    private static IEnumerable<ListBoxItem> VisibleMessages(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ListBoxItem item) yield return item; else foreach (var nested in VisibleMessages(child)) yield return nested; }
    }
    private void QueueLatest()
    {
        if (_tailQueued) return; _tailQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _tailQueued = false;
            if (!_followLatest || _restorePending || _messageScroll is null) return;
            _scrollingToLatest = true;
            _userScrolling = false;
            _messageScroll.ScrollToEnd(); JumpLatestButton.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => _scrollingToLatest = false));
        }));
    }
    private void ChatScrolled(object sender, ScrollChangedEventArgs args)
    {
        if (_messageScroll is null || _restoring || _restorePending) return;
        if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0) { if (_followLatest) QueueLatest(); return; }
        if (_scrollingToLatest) return;
        if (!_userScrolling) { if (_followLatest && _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset > 5) QueueLatest(); return; }
        _followLatest = _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset <= 60;
        if (_followLatest) _userScrolling = false;
        JumpLatestButton.Visibility = _followLatest ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UserWheel(object sender, MouseWheelEventArgs args)
    { CancelReadRestoration(); _userScrolling = true; if (args.Delta > 0) _followLatest = false; }
    private void CancelReadRestoration() { _restoreGeneration++; _restorePending = _restoring = false; }
    private void UserScrollKey(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) CancelReadRestoration();
        if (args.Key is Key.Up or Key.PageUp or Key.Home) { _userScrolling = true; _followLatest = false; }
        else if (args.Key is Key.Down or Key.PageDown or Key.End) _userScrolling = true;
    }
    private void UserScrollbar(object sender, MouseButtonEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source != _messageScroll)
        { if (source is System.Windows.Controls.Primitives.ScrollBar) { CancelReadRestoration(); _userScrolling = true; _followLatest = false; break; } source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); }
    }
    private void JumpLatest_Click(object sender, RoutedEventArgs args) { CancelReadRestoration(); _followLatest = true; QueueLatest(); }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer viewer) return viewer; if (FindScroll(child) is { } nested) return nested; }
        return null;
    }
    private void Composer_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is DiscordFriendsViewModel model && model.SendMessageCommand.CanExecute(null)) model.SendMessageCommand.Execute(null);
        args.Handled = true;
    }
    private void CompactParty_Click(object sender, RoutedEventArgs args)
    { if (sender is UIElement target) CompactPartyPopup.PlacementTarget = target; CompactPartyPopup.IsOpen = !CompactPartyPopup.IsOpen; }
    private void GifPicker_Click(object sender, RoutedEventArgs args) => GifPickerPopup.IsOpen = !GifPickerPopup.IsOpen;
    internal bool CloseTransientPanels()
    {
        if (GifPickerPopup.IsOpen) { GifPickerPopup.IsOpen = false; return true; }
        if (CompactPartyPopup.IsOpen) { CompactPartyPopup.IsOpen = false; return true; }
        return false;
    }
    private void GifPicked(object? sender, string link)
    {
        if (DataContext is not DiscordFriendsViewModel model || model.MessageDraft.Length + link.Length + 1 > 2000) return;
        model.MessageDraft = model.MessageDraft.Length == 0 ? link : model.MessageDraft.TrimEnd() + "\n" + link;
        GifPickerPopup.IsOpen = false; MessageComposer.Focus(); MessageComposer.CaretIndex = model.MessageDraft.Length;
    }
    internal void RevealParty() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
    {
        if (PartyPane.Visibility == Visibility.Collapsed) CompactPartyPopup.IsOpen = true;
        else if (PartyPane.Child is SocialPartyPanel panel) panel.ShowInvitations();
    }));
    private void AccountMenu_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(new MenuItem { Header = "Disconnect Discord", Command = model.DisconnectCommand });
        menu.IsOpen = true;
    }
    private void ConversationMenu_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || sender is not FrameworkElement button) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Reload conversation", Command = model.ReloadChatCommand });
        if (model.SelectedFriend is { } friend)
        {
            menu.Items.Add(new MenuItem { Header = "Open in Discord", Command = model.OpenDiscordMessageCommand, CommandParameter = model.Messages.LastOrDefault() });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Remove friend", Command = model.RemoveCommand, CommandParameter = friend });
            menu.Items.Add(new MenuItem { Header = "Block on Discord", Command = model.BlockCommand, CommandParameter = friend });
        }
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void CopyMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { CommandParameter: string text } || text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(Window.GetWindow(this), "The clipboard is busy. Try copying again.", "Copy message", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}

public sealed class SocialFilterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}
public sealed class SocialDayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTime day ? day.Date == DateTime.Today ? "Today" : day.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("d MMM yyyy", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class SocialUnreadConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    { var count = values.Length >= 2 && values[0] is ulong id && values[1] is DiscordFriendsViewModel model ? model.UnreadMessages(id) : 0; return count == 0 ? "" : count > 99 ? "99+" : count.ToString(); }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
