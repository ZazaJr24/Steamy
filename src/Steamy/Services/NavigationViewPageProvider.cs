using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Abstractions;

namespace Steamy.Services;

/// <summary>Creates WPF pages requested by WPF-UI's NavigationView.</summary>
public sealed class NavigationViewPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    private Dictionary<Type, FrameworkElement>? _pages;
    private readonly LinkedList<Type> _recent = new();
    private readonly Dictionary<Type, Dictionary<string, Point>> _positions = new();
    private FrameworkElement? _transientPage;
    private Action<FrameworkElement>? _release;
    internal int CachedPageCount => (_pages?.Count ?? 0) + (_transientPage is null ? 0 : 1);
    internal NavigationViewPageProvider CreateWindowScope(Action<FrameworkElement>? release = null) => new(services) { _pages = [], _release = release };
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        if (!DiscordIntegration.Enabled && DiscordIntegration.IsSocialPage(pageType)) return null;
        if (!typeof(FrameworkElement).IsAssignableFrom(pageType)) return null;
        if (_transientPage is not null) { _release?.Invoke(_transientPage); _transientPage = null; }
        // Tool pages have the same Loaded/Unloaded lifetime as the main views. Keep
        // recently visited routes in this window's bounded cache as well. Game details
        // carry a selected game and remain transient rather than retaining stale input.
        var reusable = typeof(Page).IsAssignableFrom(pageType) && pageType != typeof(Pages.GameDetailsPage);
        if (reusable && _pages?.TryGetValue(pageType, out var previous) == true)
        { _recent.Remove(pageType); _recent.AddFirst(pageType); return previous; }
        var page = (FrameworkElement)ActivatorUtilities.CreateInstance(services, pageType);
        if (reusable && _pages is not null)
        {
            _pages[pageType] = page; _recent.AddFirst(pageType);
            page.Unloaded += (_, _) =>
            {
                var state = new Dictionary<string, Point>();
                foreach (var scroll in Scrolls(page))
                    if (ScrollKey(scroll) is { Length: > 0 } key)
                        state[key] = new(scroll.HorizontalOffset, scroll.VerticalOffset);
                _positions[pageType] = state;
                Controls.ArtworkLease.SetImagesActive(page, false);
            };
            page.Loaded += (_, _) => page.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!page.IsLoaded) return;
                Controls.ArtworkLease.SetImagesActive(page, true);
                if (!_positions.TryGetValue(pageType, out var positions)) return;
                foreach (var scroll in Scrolls(page))
                    if (positions.TryGetValue(ScrollKey(scroll), out var position))
                    { scroll.ScrollToHorizontalOffset(position.X); scroll.ScrollToVerticalOffset(position.Y); }
            }));
            // The social dock has its own owner. Keep at most three main page visuals.
            Trim(3);
        }
        else if (_pages is not null) { _transientPage = page; Trim(2); }
        return page;
    }
    private void Trim(int capacity)
    {
        while (_pages!.Count > capacity && _recent.Last is not null)
        {
            var type = _recent.Last.Value;
            _recent.Remove(type); if (_pages.Remove(type, out var evicted)) _release?.Invoke(evicted);
        }
    }
    private static string ScrollKey(ScrollViewer scroll) => scroll.TemplatedParent is FrameworkElement { Name.Length: > 0 } parent
        ? parent.Name + "/" + scroll.Name : scroll.Name;
    private static IEnumerable<ScrollViewer> Scrolls(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) yield return scroll;
            foreach (var nested in Scrolls(child)) yield return nested;
        }
    }
}
