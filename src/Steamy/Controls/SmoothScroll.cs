using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;

namespace Steamy.Controls;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty HorizontalProperty = DependencyProperty.RegisterAttached("Horizontal", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));
    public static bool GetHorizontal(DependencyObject owner) => (bool)owner.GetValue(HorizontalProperty);
    public static void SetHorizontal(DependencyObject owner, bool value) => owner.SetValue(HorizontalProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(ScrollState), typeof(SmoothScroll));
    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);

    internal static double SnapOffset(double offset, double maximum, double dpiScale)
        => offset >= maximum ? Math.Max(0, maximum) : Math.Clamp(Math.Round(offset * dpiScale) / dpiScale, 0, Math.Max(0, maximum));

    private static void OnEnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is Control control && owner is not ScrollViewer)
        {
            control.Loaded -= ControlLoaded; control.Loaded += ControlLoaded;
            if (control.IsLoaded) ControlLoaded(control, new RoutedEventArgs());
            return;
        }
        if (owner is not ScrollViewer viewer) return;
        if (viewer.GetValue(StateProperty) is ScrollState previous) previous.Detach();
        viewer.SetValue(StateProperty, (bool)args.NewValue ? new ScrollState(viewer) : null);
    }
    private static void ControlLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Control control) return;
        ScrollViewer? Find(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer scroll) return scroll; if (Find(child) is { } nested) return nested; }
            return null;
        }
        if (Find(control) is { } viewer) SetEnabled(viewer, GetEnabled(control));
    }

    private static bool PixelVirtualized(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is VirtualizingGamePanel) return true;
            if (child is VirtualizingStackPanel panel && ItemsControl.GetItemsOwner(panel) is { } owner && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel) return true;
            if (child is not ScrollViewer && PixelVirtualized(child)) return true;
        }
        return false;
    }

    private sealed class ScrollState
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        private readonly ScrollMotion _motion = new();
        private long _lastTick;
        private TimeSpan? _lastRenderTime;
        private double _expectedOffset;
        private Window? _window;
        private bool _animating;
        private bool _preferencesAttached;
        private int _direction;
        private double CurrentOffset => GetHorizontal(_viewer) ? _viewer.HorizontalOffset : _viewer.VerticalOffset;
        private double MaximumOffset => GetHorizontal(_viewer) ? _viewer.ScrollableWidth : _viewer.ScrollableHeight;
        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.PreviewMouseWheel += OnWheel;
            viewer.Unloaded += OnUnloaded;
            viewer.Loaded += OnLoaded;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.SizeChanged += OnSizeChanged;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.IsVisibleChanged += OnVisibilityChanged;
            if (viewer.IsLoaded) OnLoaded(viewer, new RoutedEventArgs());
        }
        public void Detach()
        {
            Stop();
            _viewer.PreviewMouseWheel -= OnWheel;
            _viewer.Unloaded -= OnUnloaded;
            _viewer.Loaded -= OnLoaded;
            _viewer.PreviewMouseDown -= OnMouseDown;
            _viewer.PreviewKeyDown -= OnKeyDown;
            _viewer.SizeChanged -= OnSizeChanged;
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.IsVisibleChanged -= OnVisibilityChanged;
            DetachWindow();
            MotionPreferences.Changed -= OnPreferencesChanged;
            _preferencesAttached = false;
        }
        private void OnPreferencesChanged(object? sender, EventArgs args) { if (!MotionPreferences.AnimationsEnabled) Stop(); }
        private void OnMouseDown(object sender, MouseButtonEventArgs args) { if (_animating) Stop(); }
        private void OnKeyDown(object sender, KeyEventArgs args) { if (_animating) Stop(); }
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) { if (_animating) Stop(); }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!_viewer.IsVisible) Stop(); }
        private void WindowPaused(object? sender, EventArgs args) => Stop();
        private void DetachWindow()
        {
            if (_window is null) return;
            _window.Deactivated -= WindowPaused; _window.StateChanged -= WindowPaused; _window = null;
        }
        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_preferencesAttached) return;
            MotionPreferences.Changed += OnPreferencesChanged;
            _preferencesAttached = true;
            _window = Window.GetWindow(_viewer);
            if (_window is not null) { _window.Deactivated += WindowPaused; _window.StateChanged += WindowPaused; }
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Stop();
            MotionPreferences.Changed -= OnPreferencesChanged;
            _preferencesAttached = false;
            DetachWindow();
        }
        private void Stop()
        {
            var offset = CurrentOffset;
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            _lastRenderTime = null;
            _motion.Reset(offset);
            _target = offset;
            _direction = 0;
        }
        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (!_animating || !ReferenceEquals(args.OriginalSource, _viewer)) return;
            var change = GetHorizontal(_viewer) ? args.HorizontalChange : args.VerticalChange;
            if (change != 0 && Math.Abs(CurrentOffset - _expectedOffset) > 24.0) Stop();
        }
        private void OnWheel(object sender, MouseWheelEventArgs args)
            => OnWheelCore(sender, args, Keyboard.Modifiers);
        private void OnWheelCore(object sender, MouseWheelEventArgs args, ModifierKeys modifiers)
        {
            if (args.Handled || modifiers != ModifierKeys.None || MaximumOffset <= 0
) return;
            // Our game panel and pixel-unit virtual stacks expose offsets in pixels.
            // Other logical panels retain their own scrolling units.
            if (_viewer.CanContentScroll && !PixelVirtualized(_viewer)) return;
            // A scrollable child or open dropdown owns the wheel. Closed selectors and
            // non-scrolling template viewers must not swallow the surrounding page's input.
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != _viewer; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            {
                if (element is ComboBox { IsDropDownOpen: true } or TextBox { AcceptsReturn: true } or RichTextBox) return;
                if (element is ScrollViewer child && (GetHorizontal(child)
                    ? args.Delta < 0 ? child.HorizontalOffset < child.ScrollableWidth : child.HorizontalOffset > 0
                    : args.Delta < 0 ? child.VerticalOffset < child.ScrollableHeight : child.VerticalOffset > 0)) return;
            }
            var current = CurrentOffset;
            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            var distance = lines < 0 ? (GetHorizontal(_viewer) ? _viewer.ViewportWidth : _viewer.ViewportHeight) : Math.Max(1, lines) * 44;
            var direction = Math.Sign(args.Delta);
            if (_animating && direction != _direction) Stop(); // Reversal responds immediately, without accumulated momentum.
            if (!_animating)
            {
                var stepSize = Math.Min(36.0, Math.Abs(args.Delta / 120d * distance) * 0.35);
                var initialStep = Math.Clamp(current - Math.Sign(args.Delta) * stepSize, 0, MaximumOffset);
                CommitOffset(initialStep);
                current = CurrentOffset;
            }
            _direction = direction;
            _target = Math.Clamp((_animating ? _target : current) - args.Delta / 120d * distance, 0, MaximumOffset);
            if (Math.Abs(_target - current) < 0.5) return;
            args.Handled = true;
            if (!MotionPreferences.AnimationsEnabled)
            {
                Stop();
                _target = Math.Clamp(current - args.Delta / 120d * distance, 0, MaximumOffset);
                CommitOffset(_target);
                return;
            }
            AnimateTo(_target);
        }
        private void AnimateTo(double target)
        {
            var current = CurrentOffset;
            var destination = Math.Clamp(target, 0, MaximumOffset);
            var now = Stopwatch.GetTimestamp();
            if (!_animating)
            {
                _motion.Reset(current); _expectedOffset = current;
                _lastRenderTime = null;
                _animating = true; CompositionTarget.Rendering += OnRendering;
            }
            else
                _motion.Advance(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;
            _target = destination;
            _motion.Retarget(destination);
            _viewer.InvalidateVisual();
        }
        private void OnRendering(object? sender, EventArgs args)
        {
            if (!_animating) return;
            if (!_viewer.IsLoaded || !_viewer.IsVisible) { Stop(); return; }
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (_lastRenderTime == time) return;
            _lastRenderTime = time;
            var now = Stopwatch.GetTimestamp();
            _motion.Advance(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;
            CommitOffset(_motion.Position);
            if (_motion.IsResting)
            { CompositionTarget.Rendering -= OnRendering; _animating = false; _lastRenderTime = null; }
            else
            { _viewer.InvalidateVisual(); }
        }
        private void CommitOffset(double offset)
        {
            var dpi = VisualTreeHelper.GetDpi(_viewer);
            var horizontal = GetHorizontal(_viewer);
            _expectedOffset = SnapOffset(offset, MaximumOffset, horizontal ? dpi.DpiScaleX : dpi.DpiScaleY);
            if (Math.Abs(CurrentOffset - _expectedOffset) < .001) return;
            if (horizontal) _viewer.ScrollToHorizontalOffset(_expectedOffset);
            else _viewer.ScrollToVerticalOffset(_expectedOffset);
        }
    }
}
