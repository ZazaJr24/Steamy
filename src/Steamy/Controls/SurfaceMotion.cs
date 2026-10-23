using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>Soft panel entrances; transforms and animation clocks are released on completion.</summary>
public static class SurfaceMotion
{
    public static readonly DependencyProperty DurationProperty=DependencyProperty.RegisterAttached("Duration",typeof(double),typeof(SurfaceMotion),new PropertyMetadata(320d),value=>value is double duration && double.IsFinite(duration) && duration>=0 && duration<=2000);
    public static readonly DependencyProperty StartScaleProperty=DependencyProperty.RegisterAttached("StartScale",typeof(double),typeof(SurfaceMotion),new PropertyMetadata(1d),value=>value is double scale && double.IsFinite(scale) && scale>=.5 && scale<=1);
    public static readonly DependencyProperty LiftProperty=DependencyProperty.RegisterAttached("Lift",typeof(double),typeof(SurfaceMotion),new PropertyMetadata(8d),value=>value is double lift && double.IsFinite(lift) && lift>=0 && lift<=40);
    public static double GetDuration(DependencyObject element)=>(double)element.GetValue(DurationProperty);
    public static void SetDuration(DependencyObject element,double value)=>element.SetValue(DurationProperty,value);
    public static double GetStartScale(DependencyObject element)=>(double)element.GetValue(StartScaleProperty);
    public static void SetStartScale(DependencyObject element,double value)=>element.SetValue(StartScaleProperty,value);
    public static double GetLift(DependencyObject element)=>(double)element.GetValue(LiftProperty);
    public static void SetLift(DependencyObject element,double value)=>element.SetValue(LiftProperty,value);
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
        if (!element.IsLoaded || !element.IsVisible || !MotionPreferences.AnimationsEnabled || GetDuration(element)==0) return;
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
        var duration=TimeSpan.FromMilliseconds(GetDuration(element));
        var startScale=GetStartScale(element);
        var opacity = new DoubleAnimation(0.35, 1, duration) { FillBehavior = FillBehavior.Stop };
        opacity.Completed += (_, _) => { if (ReferenceEquals(element.GetValue(StateProperty), state)) Reset(element); };
        IEasingFunction easing=startScale==1 ? new CubicEase {EasingMode=EasingMode.EaseOut} : new BackEase {Amplitude=.15,EasingMode=EasingMode.EaseOut};
        if(startScale!=1)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(startScale,1,duration){EasingFunction=easing,FillBehavior=FillBehavior.Stop});
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(startScale,1,duration){EasingFunction=easing,FillBehavior=FillBehavior.Stop});
        }
        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(GetLift(element), 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
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
