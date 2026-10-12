using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>Soft panel entrances; transforms and animation clocks are released on completion.</summary>
public static class SurfaceMotion
{
    public static readonly DependencyProperty RevealOnShowProperty = DependencyProperty.RegisterAttached(
        "RevealOnShow", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SurfaceMotion));
    public static bool GetRevealOnShow(DependencyObject element) => (bool)element.GetValue(RevealOnShowProperty);
    public static void SetRevealOnShow(DependencyObject element, bool value) => element.SetValue(RevealOnShowProperty, value);
    private sealed record State(Transform Original, Point Origin, ScaleTransform Scale, TranslateTransform Lift, EventHandler Preferences);

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.IsVisibleChanged -= VisibilityChanged;
        element.Loaded -= Loaded;
        if ((bool)args.NewValue)
        {
            element.IsVisibleChanged += VisibilityChanged;
            element.Loaded += Loaded;
        }
        else Reset(element);
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Reveal((FrameworkElement)sender);
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if ((bool)args.NewValue) Reveal((FrameworkElement)sender);
        else Reset((FrameworkElement)sender);
    }
    private static void Unloaded(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);

    public static void Reveal(FrameworkElement element)
    {
        Reset(element);
        if (!element.IsLoaded || !element.IsVisible || !MotionPreferences.AnimationsEnabled) return;
        var scale = new ScaleTransform(1, 1);
        var lift = new TranslateTransform();
        EventHandler preferences = (_, _) => { if (!MotionPreferences.AnimationsEnabled) Reset(element); };
        var state = new State(element.RenderTransform, element.RenderTransformOrigin, scale, lift, preferences);
        var transform = new TransformGroup();
        transform.Children.Add(state.Original);
        transform.Children.Add(scale);
        transform.Children.Add(lift);
        element.RenderTransformOrigin = new Point(0.5, 0.25);
        element.RenderTransform = transform;
        element.SetValue(StateProperty, state);
        element.Unloaded += Unloaded;
        MotionPreferences.Changed += preferences;
        var opacity = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(320)) { FillBehavior = FillBehavior.Stop };
        opacity.Completed += (_, _) => { if (ReferenceEquals(element.GetValue(StateProperty), state)) Reset(element); };
        var easing = new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        element.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Reset(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state) return;
        element.ClearValue(StateProperty);
        element.Unloaded -= Unloaded;
        MotionPreferences.Changed -= state.Preferences;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Lift.BeginAnimation(TranslateTransform.YProperty, null);
        element.RenderTransform = state.Original;
        element.RenderTransformOrigin = state.Origin;
    }
}
