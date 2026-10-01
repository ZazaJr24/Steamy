using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(ScrollState), typeof(SmoothScroll));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d, (owner, args) => ((ScrollViewer)owner).ScrollToVerticalOffset((double)args.NewValue)));
    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ScrollViewer viewer) return;
        if (viewer.GetValue(StateProperty) is ScrollState previous) previous.Detach();
        viewer.SetValue(StateProperty, (bool)args.NewValue ? new ScrollState(viewer) : null);
    }

    private sealed class ScrollState
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        private int _generation;
        private bool _animating;
        private bool _preferencesAttached;
        private int _direction;
        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.PreviewMouseWheel += OnWheel;
            viewer.Unloaded += OnUnloaded;
            viewer.Loaded += OnLoaded;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.SizeChanged += OnSizeChanged;
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
            MotionPreferences.Changed -= OnPreferencesChanged;
            _preferencesAttached = false;
        }
        private void OnPreferencesChanged(object? sender, EventArgs args) { if (!MotionPreferences.AnimationsEnabled) Stop(); }
        private void OnMouseDown(object sender, MouseButtonEventArgs args) { if (_animating) Stop(); }
        private void OnKeyDown(object sender, KeyEventArgs args) { if (_animating) Stop(); }
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) { if (_animating) Stop(); }
        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_preferencesAttached) return;
            MotionPreferences.Changed += OnPreferencesChanged;
            _preferencesAttached = true;
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Stop();
            MotionPreferences.Changed -= OnPreferencesChanged;
            _preferencesAttached = false;
        }
        private void Stop()
        {
            _generation++;
            var offset = _viewer.VerticalOffset;
            _viewer.SetCurrentValue(OffsetProperty, offset);
            _viewer.BeginAnimation(OffsetProperty, null);
            _animating = false;
            _target = offset;
            _direction = 0;
        }
        private void OnWheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || Keyboard.Modifiers != ModifierKeys.None || _viewer.ScrollableHeight <= 0
                || _viewer.CanContentScroll) return; // Logical/virtualized lists own their offset units.
            // A scrollable child or open dropdown owns the wheel. Closed selectors and
            // non-scrolling template viewers must not swallow the surrounding page's input.
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != _viewer; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            {
                if (element is ComboBox { IsDropDownOpen: true } or TextBox { AcceptsReturn: true } or RichTextBox) return;
                if (element is ScrollViewer child && (args.Delta < 0
                    ? child.VerticalOffset < child.ScrollableHeight : child.VerticalOffset > 0)) return;
            }
            var current = _viewer.VerticalOffset;
            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            var distance = lines < 0 ? _viewer.ViewportHeight : Math.Max(1, lines) * 18;
            var direction = Math.Sign(args.Delta);
            if (_animating && direction != _direction) Stop(); // Reversal responds immediately, without accumulated momentum.
            _direction = direction;
            _target = Math.Clamp((_animating ? _target : current) - args.Delta / 120d * distance, 0, _viewer.ScrollableHeight);
            if (Math.Abs(_target - current) < 0.5) return;
            args.Handled = true;
            var generation = ++_generation;
            if (!MotionPreferences.AnimationsEnabled)
            {
                _viewer.BeginAnimation(OffsetProperty, null);
                _viewer.SetCurrentValue(OffsetProperty, _target);
                _animating = false;
                return;
            }
            _animating = true;
            var animation = new DoubleAnimation(current, _target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) =>
            {
                if (generation != _generation) return;
                _viewer.SetCurrentValue(OffsetProperty, _target);
                _viewer.BeginAnimation(OffsetProperty, null);
                _animating = false;
            };
            _viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
