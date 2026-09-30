using System.Windows;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

/// <summary>One short entrance per load, with no retained clocks or hidden initial content.</summary>
public static class EntranceMotion
{
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(EntranceMotion), new PropertyMetadata(-1, OnDelayChanged));

    public static int GetDelay(DependencyObject element) => (int)element.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject element, int value) => element.SetValue(DelayProperty, value);

    private static void OnDelayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        if ((int)args.NewValue < 0) return;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (!SystemParameters.ClientAreaAnimation || !element.IsVisible) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            BeginTime = TimeSpan.FromMilliseconds(GetDelay(element)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args) =>
        ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);
}
