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
    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale", typeof(double), typeof(CardMotion), new PropertyMetadata(1.015d));
    public static double GetHoverScale(DependencyObject element) => (double)element.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject element, double value) => element.SetValue(HoverScaleProperty, value);
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(CardMotion), new PropertyMetadata(-3d));
    public static double GetHoverLift(DependencyObject element) => (double)element.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject element, double value) => element.SetValue(HoverLiftProperty, value);
    public static readonly DependencyProperty HoverDurationProperty = DependencyProperty.RegisterAttached(
        "HoverDuration", typeof(double), typeof(CardMotion), new PropertyMetadata(280d));
    public static double GetHoverDuration(DependencyObject element) => (double)element.GetValue(HoverDurationProperty);
    public static void SetHoverDuration(DependencyObject element, double value) => element.SetValue(HoverDurationProperty, value);
    public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.RegisterAttached(
        "ExpandedWidth", typeof(double), typeof(CardMotion), new PropertyMetadata(0d));
    public static double GetExpandedWidth(DependencyObject element) => (double)element.GetValue(ExpandedWidthProperty);
    public static void SetExpandedWidth(DependencyObject element, double value) => element.SetValue(ExpandedWidthProperty, value);
    public static readonly DependencyProperty CollapsedWidthProperty = DependencyProperty.RegisterAttached(
        "CollapsedWidth", typeof(double), typeof(CardMotion), new PropertyMetadata(96d, CollapsedWidthChanged));
    public static double GetCollapsedWidth(DependencyObject element) => (double)element.GetValue(CollapsedWidthProperty);
    public static void SetCollapsedWidth(DependencyObject element, double value) => element.SetValue(CollapsedWidthProperty, value);
    private static void CollapsedWidthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && element.GetValue(StateProperty) is MotionState && GetExpandedWidth(element) > 0)
            Update(element);
    }

    private sealed class MotionState
    {
        public readonly ScaleTransform Scale = new();
        public readonly TranslateTransform Lift = new();
        public required FrameworkElement Target;
        public Transform? Original;
        public Point OriginalOrigin;
        public bool Pressed;
        public FrameworkElement? Reveal;
        public readonly TranslateTransform RevealSlide = new(0, 10);
        public int Revision;
        public bool ExpandingTile;
        public double HoverDuration;
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
        var state = new MotionState { Target = target, Original = target.RenderTransform, OriginalOrigin = target.RenderTransformOrigin, ExpandingTile = GetExpandedWidth(element) > 0,
            HoverDuration = Math.Clamp(GetHoverDuration(element), 60, 500) };
        if (element is Control revealControl && revealControl.Template?.FindName("HoverDetails", revealControl) is FrameworkElement reveal)
        {
            state.Reveal = reveal;
            reveal.RenderTransform = state.RevealSlide;
        }
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
        // Containers can be reloaded while scrolling or filtering. Keep their images
        // fully visible; movement is reserved for deliberate hover and press actions.
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
        var scale = motion && state.Pressed ? 0.98 : hover ? GetHoverScale(element) : 1;
        var lift = motion && state.Pressed ? -0.5 : hover ? GetHoverLift(element) : 0;
        var revision = ++state.Revision;
        if (GetExpandedWidth(element) > 0)
            Animate(element, FrameworkElement.WidthProperty, element.IsMouseOver || element.IsKeyboardFocusWithin
                ? Math.Max(GetCollapsedWidth(element), GetExpandedWidth(element)) : GetCollapsedWidth(element), state, revision);
        Animate(state.Scale, ScaleTransform.ScaleXProperty, scale, state, revision);
        Animate(state.Scale, ScaleTransform.ScaleYProperty, scale, state, revision);
        Animate(state.Lift, TranslateTransform.YProperty, lift, state, revision);
        if (state.Reveal is not null)
        {
            var open = element.IsMouseOver || element.IsKeyboardFocusWithin;
            Animate(state.Reveal, UIElement.OpacityProperty, open ? 1 : 0, state, revision);
            Animate(state.RevealSlide, TranslateTransform.YProperty, open ? 0 : 10, state, revision);
        }
    }
    private static void Animate(IAnimatable target, DependencyProperty property, double value, MotionState state, int revision)
    {
        var dependency = (DependencyObject)target;
        var previous = (double)dependency.GetValue(property);
        dependency.SetValue(property, value);
        if (!MotionPreferences.AnimationsEnabled)
        {
            target.BeginAnimation(property, null);
            return;
        }
        var animation = new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(state.Pressed ? 90 : state.HoverDuration))
        {
            EasingFunction = state.ExpandingTile && !state.Pressed ? new SineEase { EasingMode = EasingMode.EaseInOut }
                : state.Pressed || property == UIElement.OpacityProperty ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut },
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
        if (GetExpandedWidth(element) > 0)
        {
            element.BeginAnimation(FrameworkElement.WidthProperty, null);
            element.ClearValue(FrameworkElement.WidthProperty);
        }
        if (state.Reveal is not null)
        {
            state.Reveal.BeginAnimation(UIElement.OpacityProperty, null);
            state.Reveal.Opacity = 0;
            state.RevealSlide.BeginAnimation(TranslateTransform.YProperty, null);
        }
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
