using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SteamContentManager.Controls;

public class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty SmoothValueProperty =
        DependencyProperty.Register(nameof(SmoothValue), typeof(double), typeof(SmoothProgressBar),
            new PropertyMetadata(0.0, OnSmoothValueChanged));

    private double _target;
    private DispatcherTimer? _timer;

    public double SmoothValue
    {
        get => (double)GetValue(SmoothValueProperty);
        set => SetValue(SmoothValueProperty, value);
    }

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SmoothProgressBar bar) return;
        bar._target = (double)e.NewValue;
        bar.EnsureTimer();
    }

    private void EnsureTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var current = Value;
        var diff = _target - current;

        if (Math.Abs(diff) < 0.02)
        {
            Value = _target;
            _timer!.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
            return;
        }

        var step = Math.Max(0.03, Math.Abs(diff) * 0.06);
        Value = diff > 0
            ? Math.Min(current + step, _target)
            : Math.Max(current - step, _target);
    }
}
