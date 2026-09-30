using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>
/// A progress bar that glides towards <see cref="SmoothValue"/> on every rendered frame. The easing
/// is time based, so it looks the same at any frame rate, and it never stalls on tiny steps.
/// </summary>
public class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty SmoothValueProperty =
        DependencyProperty.Register(nameof(SmoothValue), typeof(double), typeof(SmoothProgressBar),
            new PropertyMetadata(0.0, OnSmoothValueChanged));

    private const double TimeConstantSeconds = 0.3;
    private const double MinimumSpeedPerSecond = 0.6;
    private const double SnapDistance = 0.005;

    private double _target;
    private bool _animating;
    private TimeSpan _lastFrame;

    // Implicit styles are matched by DefaultStyleKey, so without this the app's ProgressBar
    // look would not apply to this subclass wherever no Style is set explicitly.
    static SmoothProgressBar() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SmoothProgressBar), new FrameworkPropertyMetadata(typeof(ProgressBar)));

    public SmoothProgressBar()
    {
        Loaded += (_, _) => Value = _target;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopAnimation();
            Value = _target;
        };
        Unloaded += (_, _) => StopAnimation();
    }

    public double SmoothValue
    {
        get => (double)GetValue(SmoothValueProperty);
        set => SetValue(SmoothValueProperty, value);
    }

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SmoothProgressBar bar) return;

        bar._target = Math.Clamp((double)e.NewValue, bar.Minimum, bar.Maximum);

        // A reset (retry, new job in a recycled container) should not crawl backwards.
        if (!bar.IsLoaded || !bar.IsVisible || !SystemParameters.ClientAreaAnimation || bar._target < bar.Value - 1)
        {
            bar.StopAnimation();
            bar.Value = bar._target;
            return;
        }

        // Sub-pixel changes would keep the composition loop hot for a bar that cannot visually
        // move; such steps are applied instantly instead of animating.
        if (Math.Abs(bar._target - bar.Value) < SnapDistance * 4)
        {
            bar.StopAnimation();
            bar.Value = bar._target;
            return;
        }

        bar.StartAnimation();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            StopAnimation();
            Value = _target;
            return;
        }
        var frameTime = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        if (frameTime == _lastFrame) return;

        var elapsed = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((frameTime - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = frameTime;

        var difference = _target - Value;
        if (Math.Abs(difference) <= SnapDistance)
        {
            Value = _target;
            StopAnimation();
            return;
        }

        var eased = Math.Abs(difference) * (1 - Math.Exp(-elapsed / TimeConstantSeconds));
        var step = Math.Min(Math.Abs(difference), Math.Max(eased, MinimumSpeedPerSecond * elapsed));
        Value += Math.Sign(difference) * step;
    }
}
