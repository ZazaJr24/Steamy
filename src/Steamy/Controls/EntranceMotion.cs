using System.Windows;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>One short entrance per load, with no retained clocks or hidden initial content.</summary>
public static class EntranceMotion
{
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(EntranceMotion), new PropertyMetadata(-1, OnDelayChanged));
    private static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached(
        "Revision", typeof(int), typeof(EntranceMotion), new PropertyMetadata(0));
    private static readonly DependencyProperty PreferencesHandlerProperty = DependencyProperty.RegisterAttached(
        "PreferencesHandler", typeof(EventHandler), typeof(EntranceMotion));

    public static int GetDelay(DependencyObject element) => (int)element.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject element, int value) => element.SetValue(DelayProperty, value);

    private static void OnDelayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        Cancel(element);
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        if ((int)args.NewValue < 0) return;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        Reveal(element, GetDelay(element));
    }

    public static void Reveal(FrameworkElement element, int delay = 0)
    {
        Cancel(element);
        if (!MotionPreferences.AnimationsEnabled || !element.IsVisible) return;
        var revision = (int)element.GetValue(RevisionProperty);
        EventHandler handler = (_, _) => { if (!MotionPreferences.AnimationsEnabled) Cancel(element); };
        element.SetValue(PreferencesHandlerProperty, handler);
        MotionPreferences.Changed += handler;
        element.Unloaded -= OnUnloaded;
        element.Unloaded += OnUnloaded;
        var animation = new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(140))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Clamp(delay, 0, 500)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if ((int)element.GetValue(RevisionProperty) == revision) Cancel(element);
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void Cancel(FrameworkElement element)
    {
        element.SetValue(RevisionProperty, (int)element.GetValue(RevisionProperty) + 1);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.GetValue(PreferencesHandlerProperty) is EventHandler handler)
        {
            MotionPreferences.Changed -= handler;
            element.ClearValue(PreferencesHandlerProperty);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args) => Cancel((FrameworkElement)sender);
}
