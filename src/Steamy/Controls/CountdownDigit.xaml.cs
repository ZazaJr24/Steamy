using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Steamy.Controls;

public partial class CountdownDigit : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(CountdownDigit), new PropertyMetadata("00", ValueChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(CountdownDigit), new PropertyMetadata("", LabelChanged));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public CountdownDigit()
    {
        InitializeComponent();
        Loaded += (_, _) => { MotionPreferences.Changed += PreferencesChanged; Number.Text = Value; Unit.Text = Label; };
        Unloaded += (_, _) => { MotionPreferences.Changed -= PreferencesChanged; Reset(); };
    }
    private static void LabelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is CountdownDigit { Unit: not null } digit) digit.Unit.Text = (string)args.NewValue;
    }
    private static void ValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not CountdownDigit digit || digit.Number is null) return;
        digit.Number.Text = (string)args.NewValue;
        digit.Reset();
        if (!digit.IsLoaded || !MotionPreferences.AnimationsEnabled) return;
        var duration = TimeSpan.FromMilliseconds(180);
        digit.DigitMotion.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        digit.Number.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, duration) { FillBehavior = FillBehavior.Stop });
    }
    private void PreferencesChanged(object? sender, EventArgs args) { if (!MotionPreferences.AnimationsEnabled) Reset(); }
    private void Reset()
    {
        DigitMotion.BeginAnimation(TranslateTransform.YProperty, null);
        Number.BeginAnimation(OpacityProperty, null);
    }
}
