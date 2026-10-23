using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>One coalesced viewport notification per scroll host, with one screen of directional prefetch.</summary>
internal static class ArtworkViewport
{
    private static readonly ConditionalWeakTable<ScrollViewer, Group> Groups = new();
    private static readonly ConditionalWeakTable<Window, WindowGroup> Windows = new();
    private static IEnumerable<ScrollViewer> Hosts(DependencyObject view)
    {
        for (var parent = VisualTreeHelper.GetParent(view); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) yield return scroll;
    }
    internal static ImagePriority? Priority(FrameworkElement view)
    {
        if (!view.IsLoaded || !view.IsVisible || Window.GetWindow(view)?.WindowState == WindowState.Minimized) return null;
        FrameworkElement reference = view;
        var localBounds = new Rect(view.RenderSize);
        if (localBounds.Width <= 0 || localBounds.Height <= 0)
        {
            // Image reports an empty RenderSize until it has a Source, even when
            // its parent has already allocated a visible slot. Do not require the
            // downloaded bitmap in order to begin downloading that first bitmap.
            if (view is not Image || VisualTreeHelper.GetParent(view) is not FrameworkElement parent) return null;
            localBounds = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(view);
            if (localBounds.Width <= 0 || localBounds.Height <= 0) return null;
            reference = parent;
        }
        var priority = ImagePriority.Visible;
        foreach (var scroll in Hosts(view))
        {
            Rect bounds;
            try { bounds = reference.TransformToAncestor(scroll).TransformBounds(localBounds); }
            catch (InvalidOperationException) { return null; }
            var width = Math.Max(1, scroll.ActualWidth); var height = Math.Max(1, scroll.ActualHeight);
            if (bounds.IntersectsWith(new Rect(0, 0, width, height))) continue;
            var backwards = Groups.TryGetValue(scroll, out var group) && group.Backwards;
            var horizontal = scroll.ScrollableWidth > 0 && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
            var reserve = horizontal ? new Rect(backwards ? -width : width, 0, width, height) : new Rect(0, backwards ? -height : height, width, height);
            if (!bounds.IntersectsWith(reserve)) return null;
            priority = ImagePriority.Prefetch;
        }
        return priority;
    }
    internal static IDisposable Observe(FrameworkElement view, Action changed)
    {
        var groups = Hosts(view).Select(scroll => Groups.GetValue(scroll, owner => new(owner))).ToArray();
        foreach (var group in groups) group.Add(changed);
        var window = Window.GetWindow(view) is { } owner ? Windows.GetValue(owner, host => new(host)) : null;
        window?.Add(changed);
        return new Subscription(groups, window, changed);
    }
    private sealed class Subscription(Group[] groups, WindowGroup? window, Action callback) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var group in groups) group.Remove(callback);
            window?.Remove(callback);
        }
    }
    private sealed class WindowGroup(Window owner)
    {
        private readonly HashSet<Action> _callbacks = [];
        internal void Add(Action callback) { if (_callbacks.Count == 0) owner.StateChanged += Changed; _callbacks.Add(callback); }
        internal void Remove(Action callback) { _callbacks.Remove(callback); if (_callbacks.Count == 0) owner.StateChanged -= Changed; }
        private void Changed(object? sender, EventArgs args)
        { foreach (var callback in _callbacks.ToArray()) callback(); }
    }
    private sealed class Group(ScrollViewer owner)
    {
        private readonly HashSet<Action> _callbacks = [];
        private DispatcherOperation? _pending;
        internal bool Backwards;
        internal void Add(Action callback) { if (_callbacks.Count == 0) owner.ScrollChanged += Changed; _callbacks.Add(callback); }
        internal void Remove(Action callback)
        { _callbacks.Remove(callback); if (_callbacks.Count == 0) { owner.ScrollChanged -= Changed; _pending?.Abort(); _pending = null; } }
        private void Changed(object sender, ScrollChangedEventArgs args)
        {
            if (args.VerticalChange != 0 || args.HorizontalChange != 0) Backwards = args.VerticalChange < 0 || args.HorizontalChange < 0;
            if (_pending is { Status: DispatcherOperationStatus.Pending }) return;
            _pending = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            { _pending = null; foreach (var callback in _callbacks.ToArray()) callback(); }));
        }
    }
}
