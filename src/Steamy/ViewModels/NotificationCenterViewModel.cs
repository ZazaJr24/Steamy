using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public enum SteamyNotificationKind { Message, PartyInvite, FriendRequest, Download, Information }
public sealed record NotificationDraft(string Key, SteamyNotificationKind Kind, string Title, string Body,
    ulong PeerId = 0, ulong LobbyId = 0, Guid InviteId = default, string? AvatarUrl = null);

public sealed class SteamyNotification : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public NotificationDraft Content { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    private bool _read;
    public bool IsRead { get => _read; internal set { if (SetProperty(ref _read, value)) OnPropertyChanged(nameof(UnreadVisibility)); } }
    public Visibility UnreadVisibility => IsRead ? Visibility.Collapsed : Visibility.Visible;
    public string Title => Content.Title;
    public string Body => Content.Body;
    public string? AvatarUrl => Content.AvatarUrl;
    public string Initial => Title.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(Title) : "S";
    public string KindLabel => Content.Kind switch { SteamyNotificationKind.Message => "Message", SteamyNotificationKind.PartyInvite => "Party invitation", SteamyNotificationKind.FriendRequest => "Friend request", SteamyNotificationKind.Download => "Download", _ => "Steamy" };
    public int MessageCount { get; private set; } = 1;
    public string CountLabel => MessageCount > 1 ? $"{MessageCount} messages" : KindLabel;
    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");
    internal SteamyNotification(NotificationDraft content, DateTimeOffset now) { Content = content; CreatedAt = now; }
    internal void Merge(NotificationDraft content, DateTimeOffset now)
    { Content = content; CreatedAt = now; MessageCount++; IsRead = false; foreach (var name in new[] { nameof(Title), nameof(Body), nameof(AvatarUrl), nameof(CountLabel), nameof(TimeLabel) }) OnPropertyChanged(name); }
}

/// <summary>Bounded, memory-only notifications. All mutations run on the UI dispatcher.</summary>
public sealed class NotificationCenterViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<Action> _dispatch;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = [];
    private readonly Dictionary<Guid, DateTimeOffset> _toastDeadlines = [];
    public ObservableCollection<SteamyNotification> Items { get; } = [];
    public ObservableCollection<SteamyNotification> Toasts { get; } = [];
    public IRelayCommand<SteamyNotification> OpenCommand { get; }
    public IRelayCommand<SteamyNotification> DismissToastCommand { get; }
    public IRelayCommand MarkAllReadCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public event EventHandler<SteamyNotification>? OpenRequested;
    public event EventHandler? Changed;
    public int UnreadCount => Items.Count(item => !item.IsRead);
    public string BadgeLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility BadgeVisibility => UnreadCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => UnreadCount == 0 ? "You're all caught up" : $"{UnreadCount} unread notification{(UnreadCount == 1 ? "" : "s")}";
    public string Tooltip => "Notifications · " + Summary;
    private bool _disposed;
    public NotificationCenterViewModel(ISettingsService settings) : this(settings, action =>
    { if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action); else action(); }) { }
    internal NotificationCenterViewModel(ISettingsService settings, Action<Action> dispatch)
    {
        _settings = settings.Load(); _dispatch = dispatch;
        OpenCommand = new RelayCommand<SteamyNotification>(item =>
        { if (item is null || !Items.Contains(item)) return; item.IsRead = true; Toasts.Remove(item); _toastDeadlines.Remove(item.Id); Notify(); OpenRequested?.Invoke(this, item); });
        DismissToastCommand = new RelayCommand<SteamyNotification>(item => { if (item is not null) { Toasts.Remove(item); _toastDeadlines.Remove(item.Id); } });
        MarkAllReadCommand = new RelayCommand(() => { foreach (var item in Items) item.IsRead = true; Toasts.Clear(); _toastDeadlines.Clear(); Notify(); });
        ClearCommand = new RelayCommand(() => { Items.Clear(); Toasts.Clear(); _toastDeadlines.Clear(); Notify(); });
    }
    public void Publish(NotificationDraft content, bool showToast = true)
    {
        _dispatch(() =>
        {
            if (_disposed || !_settings.Notifications || !_seen.Add(content.Key)) return;
            content = content with { Title = Limit(content.Title, 90), Body = Limit(content.Body, 160) };
            _seenOrder.Enqueue(content.Key); while (_seenOrder.Count > 512) _seen.Remove(_seenOrder.Dequeue());
            var now = DateTimeOffset.UtcNow;
            var existing = content.Kind == SteamyNotificationKind.Message ? Items.FirstOrDefault(item => !item.IsRead && item.Content.Kind == content.Kind
                && item.Content.PeerId == content.PeerId && item.Content.LobbyId == content.LobbyId && now - item.CreatedAt < TimeSpan.FromSeconds(8)) : null;
            if (existing is not null) { existing.Merge(content, now); Items.Move(Items.IndexOf(existing), 0); }
            var entry = existing ?? new SteamyNotification(content with { Title = Limit(content.Title, 90), Body = Limit(content.Body, 160) }, now);
            if (existing is null) Items.Insert(0, entry);
            while (Items.Count > 80) { var old = Items[^1]; Items.RemoveAt(Items.Count - 1); Toasts.Remove(old); _toastDeadlines.Remove(old.Id); }
            if (showToast)
            {
                Toasts.Remove(entry); Toasts.Insert(0, entry); _toastDeadlines[entry.Id] = now.AddSeconds(7);
                while (Toasts.Count > 3) { _toastDeadlines.Remove(Toasts[^1].Id); Toasts.RemoveAt(Toasts.Count - 1); }
            }
            Notify();
        });
    }
    public void MarkConversationRead(ulong peerId, ulong lobbyId = 0) => _dispatch(() =>
    { foreach (var item in Items.Where(item => item.Content.Kind == SteamyNotificationKind.Message && (lobbyId > 0 ? item.Content.LobbyId == lobbyId : item.Content.LobbyId == 0 && item.Content.PeerId == peerId))) { item.IsRead = true; Toasts.Remove(item); _toastDeadlines.Remove(item.Id); } Notify(); });
    public int ConversationUnreadCount(ulong peerId) => Items.Where(item => !item.IsRead && item.Content.Kind == SteamyNotificationKind.Message && item.Content.LobbyId == 0 && item.Content.PeerId == peerId).Sum(item => item.MessageCount);
    public void ClearSocial() => _dispatch(() =>
    {
        foreach (var item in Items.Where(item => item.Content.Kind is SteamyNotificationKind.Message or SteamyNotificationKind.PartyInvite or SteamyNotificationKind.FriendRequest).ToArray()) { Items.Remove(item); Toasts.Remove(item); _toastDeadlines.Remove(item.Id); }
        foreach (var key in _seen.Where(key => key.StartsWith("social:", StringComparison.Ordinal)).ToArray()) _seen.Remove(key);
        var remaining = _seenOrder.Where(key => !key.StartsWith("social:", StringComparison.Ordinal)).ToArray(); _seenOrder.Clear(); foreach (var key in remaining) _seenOrder.Enqueue(key);
        Notify();
    });
    public void ResolveSocialTarget(SteamyNotificationKind kind, ulong peerId = 0, Guid inviteId = default) => _dispatch(() =>
    { foreach (var item in Items.Where(item => item.Content.Kind == kind && (kind == SteamyNotificationKind.PartyInvite ? item.Content.InviteId == inviteId : item.Content.PeerId == peerId))) { item.IsRead = true; Toasts.Remove(item); _toastDeadlines.Remove(item.Id); } Notify(); });
    internal void ExpireToasts(DateTimeOffset now)
    { foreach (var item in Toasts.Where(item => !_settings.Notifications || _toastDeadlines.GetValueOrDefault(item.Id) <= now).ToArray()) { Toasts.Remove(item); _toastDeadlines.Remove(item.Id); } }
    private static string Limit(string value, int length) => value.Length > length ? value[..length] + "…" : value;
    private void Notify()
    { foreach (var name in new[] { nameof(UnreadCount), nameof(BadgeLabel), nameof(BadgeVisibility), nameof(EmptyVisibility), nameof(Summary), nameof(Tooltip) }) OnPropertyChanged(name); Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose() { _disposed = true; Items.Clear(); Toasts.Clear(); _toastDeadlines.Clear(); _seen.Clear(); _seenOrder.Clear(); }
}

public sealed class DesktopNotificationService(NotificationCenterViewModel center, ILoggingService logging) : INotificationService
{
    public void Show(string title, string message)
    {
        logging.Add(LogLevel.Info, "Notification", title + ": " + message);
        center.Publish(new(Guid.NewGuid().ToString(), title == "Download completed" ? SteamyNotificationKind.Download : SteamyNotificationKind.Information, title, message));
    }
}
