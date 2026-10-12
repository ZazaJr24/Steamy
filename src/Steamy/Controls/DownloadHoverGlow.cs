using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

// A 120 ms highlight on one overlay visual, with no permanent render loop.
public static class DownloadHoverGlow
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DownloadHoverGlow), new PropertyMetadata(false, Enable));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(GlowState), typeof(DownloadHoverGlow));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    private static void Enable(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not FrameworkElement overlay) return;
        if (overlay.GetValue(StateProperty) is GlowState old) old.Stop();
        if ((bool)change.NewValue) overlay.SetValue(StateProperty, new GlowState(overlay));
    }
    private sealed class GlowState
    {
        private readonly FrameworkElement _overlay;
        private UIElement? _owner;
        private int _revision;
        public GlowState(FrameworkElement overlay) { _overlay = overlay; overlay.Loaded += Load; overlay.Unloaded += Unload; }
        private void Load(object sender, RoutedEventArgs args)
        {
            DependencyObject? parent = _overlay;
            while (parent is not null && parent is not (ButtonBase or ListBoxItem)) parent = VisualTreeHelper.GetParent(parent);
            _owner = parent as UIElement;
            if (_owner is null) return;
            _owner.MouseEnter += Hover; _owner.MouseLeave += Hover;
            MotionPreferences.Changed += PreferencesChanged;
            SetHighlight(false);
        }
        private void Hover(object sender, System.Windows.Input.MouseEventArgs args) => SetHighlight(true);
        private void PreferencesChanged(object? sender, EventArgs args) => SetHighlight(false);
        private void SetHighlight(bool animate)
        {
            var target = _owner?.IsMouseOver == true && _owner.IsEnabled ? 1d : 0d;
            var current = _overlay.Opacity;
            var revision = ++_revision;
            _overlay.BeginAnimation(UIElement.OpacityProperty, null); _overlay.Opacity = target;
            if (!animate || !MotionPreferences.AnimationsEnabled) return;
            var motion = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop };
            motion.Completed += (_, _) => { if (_revision == revision) _overlay.BeginAnimation(UIElement.OpacityProperty, null); };
            _overlay.BeginAnimation(UIElement.OpacityProperty, motion);
        }
        private void Unload(object sender, RoutedEventArgs args)
        {
            if (_owner is not null) { _owner.MouseEnter -= Hover; _owner.MouseLeave -= Hover; }
            _owner = null; MotionPreferences.Changed -= PreferencesChanged;
            ++_revision; _overlay.BeginAnimation(UIElement.OpacityProperty, null); _overlay.Opacity = 0;
        }
        public void Stop() { Unload(this, new RoutedEventArgs()); _overlay.Loaded -= Load; _overlay.Unloaded -= Unload; }
    }
}
