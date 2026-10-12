using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>One glass highlight follows pointer or keyboard focus without moving the buttons.</summary>
public static class PillMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PillMotion), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(PillMotion));
    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.RegisterAttached("SelectedIndex", typeof(int), typeof(PillMotion), new PropertyMetadata(0, SelectionChanged));
    public static int GetSelectedIndex(DependencyObject value) => (int)value.GetValue(SelectedIndexProperty);
    public static void SetSelectedIndex(DependencyObject value, int index) => value.SetValue(SelectedIndexProperty, index);
    private static void SelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => (sender.GetValue(StateProperty) as State)?.Select();
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Grid grid) return;
        grid.Loaded -= Loaded; grid.Unloaded -= Unloaded;
        Unloaded(grid, new RoutedEventArgs());
        if (!(bool)args.NewValue) return;
        grid.Loaded += Loaded; grid.Unloaded += Unloaded;
        if (grid.IsLoaded) Loaded(grid, new RoutedEventArgs());
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender;
        if (grid.GetValue(StateProperty) is State) return;
        if (grid.Children.OfType<Border>().FirstOrDefault(b => b.Name == "NavigationHighlight") is not { } highlight) return;
        var buttons = grid.Children.OfType<Button>().ToArray();
        if (buttons.Length == 0) return;
        var state = new State(grid, highlight, buttons);
        grid.SetValue(StateProperty, state);
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender;
        (grid.GetValue(StateProperty) as State)?.Dispose();
        grid.ClearValue(StateProperty);
    }
    private sealed class State : IDisposable
    {
        private readonly Grid _grid;
        private readonly Border _highlight;
        private readonly Button[] _buttons;
        private readonly TranslateTransform _position = new();
        private Button _target;
        public State(Grid grid, Border highlight, Button[] buttons)
        {
            _grid = grid; _highlight = highlight; _buttons = buttons; _target = buttons[0];
            highlight.RenderTransform = _position;
            foreach (var button in buttons) { button.MouseEnter += Enter; button.GotKeyboardFocus += Focus; }
            grid.MouseLeave += Leave; grid.SizeChanged += Resize;
            MotionPreferences.Changed += Preferences;
            Select();
        }
        private void Enter(object sender, MouseEventArgs args) => Move((Button)sender, true);
        private void Focus(object sender, KeyboardFocusChangedEventArgs args) => Move((Button)sender, true);
        public void Select() => Move(_buttons[Math.Clamp(GetSelectedIndex(_grid), 0, _buttons.Length - 1)], false);
        private void Leave(object sender, MouseEventArgs args) => Move(_buttons[Math.Clamp(GetSelectedIndex(_grid), 0, _buttons.Length - 1)], true);
        private void Resize(object sender, SizeChangedEventArgs args) => Move(_target, false);
        private void Preferences(object? sender, EventArgs args) => Move(_target, false);
        private void Move(Button target, bool animate)
        {
            _target = target;
            var x = target.TranslatePoint(new Point(), _grid).X;
            var width = Math.Max(1, target.ActualWidth);
            if (animate && MotionPreferences.AnimationsEnabled)
            {
                var duration = TimeSpan.FromMilliseconds(175);
                var easing = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut };
                var startX = _position.X;
                var startWidth = double.IsNaN(_highlight.Width) ? width : _highlight.Width;
                _position.X = x; _highlight.Width = width;
                _position.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(startX, x, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
                _highlight.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(startWidth, width, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            }
            else
            {
                _position.BeginAnimation(TranslateTransform.XProperty, null);
                _highlight.BeginAnimation(FrameworkElement.WidthProperty, null);
                _position.X = x; _highlight.Width = width;
            }
        }
        public void Dispose()
        {
            foreach (var button in _buttons) { button.MouseEnter -= Enter; button.GotKeyboardFocus -= Focus; }
            _grid.MouseLeave -= Leave; _grid.SizeChanged -= Resize;
            MotionPreferences.Changed -= Preferences;
            _position.BeginAnimation(TranslateTransform.XProperty, null);
            _highlight.BeginAnimation(FrameworkElement.WidthProperty, null);
        }
    }
}
