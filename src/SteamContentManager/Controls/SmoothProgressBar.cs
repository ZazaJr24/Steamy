using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SteamContentManager.Controls;

public class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty SmoothValueProperty =
        DependencyProperty.Register(nameof(SmoothValue), typeof(double), typeof(SmoothProgressBar),
            new PropertyMetadata(0.0, OnSmoothValueChanged));

    public double SmoothValue
    {
        get => (double)GetValue(SmoothValueProperty);
        set => SetValue(SmoothValueProperty, value);
    }

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SmoothProgressBar bar) return;
        var animation = new DoubleAnimation((double)e.NewValue, new Duration(TimeSpan.FromMilliseconds(500)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(ValueProperty, animation);
    }
}
