using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>Short, interruptible card movement. Idle cards retain no animation clocks.</summary>
public static class CardMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CardMotion), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(CardMotion));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private sealed class MotionState
    {
        public readonly ScaleTransform Scale = new();
        public readonly TranslateTransform Lift = new();
        public required FrameworkElement Target;
        public Transform? Original;
        public Point OriginalOrigin;
        public bool Pressed;
        public int Revision;
        public EventHandler? PreferencesChanged;
    }

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        element.Unloaded -= Unloaded;
        Detach(element);
        if (!(bool)args.NewValue) { Reset(element); return; }
        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
        if (element.IsLoaded) Loaded(element, new RoutedEventArgs());
    }

    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (element.GetValue(StateProperty) is MotionState) return;
        var target = element;
        if (element is Control control)
        {
            control.ApplyTemplate();
            target = control.Template?.FindName("MotionSurface", control) as FrameworkElement ?? element;
        }
        var state = new MotionState { Target = target, Original = target.RenderTransform, OriginalOrigin = target.RenderTransformOrigin };
        var group = new TransformGroup();
        if (state.Original is not null) group.Children.Add(state.Original);
        group.Children.Add(state.Scale);
        group.Children.Add(state.Lift);
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        target.RenderTransform = group;
        element.SetValue(StateProperty, state);
        state.PreferencesChanged = (_, _) =>
        {
            if (!MotionPreferences.AnimationsEnabled) element.BeginAnimation(UIElement.OpacityProperty, null);
            Update(element);
        };
        MotionPreferences.Changed += state.PreferencesChanged;
        element.MouseEnter += Changed;
        element.MouseLeave += Changed;
        element.GotKeyboardFocus += FocusChanged;
        element.LostKeyboardFocus += FocusChanged;
        element.PreviewMouseLeftButtonDown += Press;
        element.PreviewMouseLeftButtonUp += Release;
        element.LostMouseCapture += Release;
        element.PreviewKeyDown += KeyDown;
        element.PreviewKeyUp += KeyUp;
        EntranceMotion.Reveal(element);
    }

    private static void Changed(object sender, MouseEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (!element.IsMouseOver && element.GetValue(StateProperty) is MotionState state) state.Pressed = false;
        Update(element);
    }
    private static void FocusChanged(object sender, KeyboardFocusChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (!element.IsKeyboardFocusWithin && element.GetValue(StateProperty) is MotionState state) state.Pressed = false;
        Update(element);
    }
    private static void Press(object sender, MouseButtonEventArgs args) => SetPressed((FrameworkElement)sender, true);
    private static void Release(object sender, MouseEventArgs args) => SetPressed((FrameworkElement)sender, false);
    private static void KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter) SetPressed((FrameworkElement)sender, true);
    }
    private static void KeyUp(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter) SetPressed((FrameworkElement)sender, false);
    }
    private static void SetPressed(FrameworkElement element, bool pressed)
    {
        if (element.GetValue(StateProperty) is not MotionState state) return;
        state.Pressed = pressed;
        Update(element);
    }
    private static void Update(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not MotionState state) return;
        var motion = MotionPreferences.AnimationsEnabled;
        var hover = motion && (element.IsMouseOver || element.IsKeyboardFocusWithin);
        var scale = motion && state.Pressed ? 0.99 : hover ? 1.005 : 1;
        var lift = motion && state.Pressed ? -0.5 : hover ? -2 : 0;
        var revision = ++state.Revision;
        Animate(state.Scale, ScaleTransform.ScaleXProperty, scale, state, revision);
        Animate(state.Scale, ScaleTransform.ScaleYProperty, scale, state, revision);
        Animate(state.Lift, TranslateTransform.YProperty, lift, state, revision);
    }
    private static void Animate(Animatable target, DependencyProperty property, double value, MotionState state, int revision)
    {
        var previous = (double)target.GetValue(property);
        target.SetValue(property, value);
        if (!MotionPreferences.AnimationsEnabled)
        {
            target.BeginAnimation(property, null);
            return;
        }
        var animation = new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(state.Pressed ? 85 : 190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => { if (revision == state.Revision) target.BeginAnimation(property, null); };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Detach(FrameworkElement element)
    {
        element.MouseEnter -= Changed;
        element.MouseLeave -= Changed;
        element.GotKeyboardFocus -= FocusChanged;
        element.LostKeyboardFocus -= FocusChanged;
        element.PreviewMouseLeftButtonDown -= Press;
        element.PreviewMouseLeftButtonUp -= Release;
        element.LostMouseCapture -= Release;
        element.PreviewKeyDown -= KeyDown;
        element.PreviewKeyUp -= KeyUp;
    }
    private static void Reset(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not MotionState state) return;
        MotionPreferences.Changed -= state.PreferencesChanged;
        state.Revision++;
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Lift.BeginAnimation(TranslateTransform.YProperty, null);
        state.Target.RenderTransform = state.Original ?? Transform.Identity;
        state.Target.RenderTransformOrigin = state.OriginalOrigin;
        element.ClearValue(StateProperty);
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        Detach(element);
        Reset(element);
    }
}
