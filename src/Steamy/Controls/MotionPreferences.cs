using System.ComponentModel;
using System.Windows;

namespace Steamy.Controls;

/// <summary>One motion policy for cards, scrolling, dialogs and the user's reduced-effects setting.</summary>
public static class MotionPreferences
{
    static MotionPreferences() => SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;

    public static bool ReduceEffects { get; private set; }
    public static bool AnimationsEnabled => !ReduceEffects && SystemParameters.ClientAreaAnimation;
    public static bool BackdropBlurEnabled => AnimationsEnabled && !SystemParameters.HighContrast;
    public static event EventHandler? Changed;

    public static void Configure(bool reduceEffects)
    {
        if (ReduceEffects == reduceEffects) return;
        ReduceEffects = reduceEffects;
        NotifyChanged();
    }

    private static void OnSystemPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            NotifyChanged();
    }

    private static void NotifyChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => Changed?.Invoke(null, EventArgs.Empty));
            return;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
