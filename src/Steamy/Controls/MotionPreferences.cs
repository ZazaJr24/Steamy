using System.ComponentModel;
using System.Windows;

namespace Steamy.Controls;

/// <summary>One motion policy for cards, scrolling, dialogs and the user's reduced-effects setting.</summary>
public static class MotionPreferences
{
    static MotionPreferences() => SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;

    public static bool ReduceEffects { get; private set; }
    public static bool TrailerPreviewsEnabled { get; private set; } = true;
    public static bool AnimationsEnabled => !ReduceEffects && SystemParameters.ClientAreaAnimation;
    // The gallery is a static snapshot: disabling motion does not disable its blur.
    public static bool BackdropBlurEnabled => !ReduceEffects && !SystemParameters.HighContrast;
    public static bool NativeBackdropEnabled => AnimationsEnabled && !SystemParameters.HighContrast;
    public static event EventHandler? Changed;

    // Template parts can disappear before receiving Unloaded. WPF's weak
    // manager lets their owner retain the callback without the global policy
    // retaining the visual tree. The existing event remains for service owners.
    private sealed class WeakNotifications
    {
        internal static readonly WeakNotifications Shared = new();
        public event EventHandler<EventArgs>? Changed;
        private WeakNotifications() => MotionPreferences.Changed += Forward;
        private void Forward(object? sender, EventArgs args) => Changed?.Invoke(this, args);
    }
    internal static void AddWeakChangedHandler(EventHandler<EventArgs> handler) =>
        WeakEventManager<WeakNotifications, EventArgs>.AddHandler(WeakNotifications.Shared, nameof(WeakNotifications.Changed), handler);
    internal static void RemoveWeakChangedHandler(EventHandler<EventArgs> handler) =>
        WeakEventManager<WeakNotifications, EventArgs>.RemoveHandler(WeakNotifications.Shared, nameof(WeakNotifications.Changed), handler);

    public static void Configure(bool reduceEffects)
    {
        if (ReduceEffects == reduceEffects) return;
        ReduceEffects = reduceEffects;
        NotifyChanged();
    }

    public static void ConfigureTrailers(bool enabled)
    {
        if (TrailerPreviewsEnabled == enabled) return;
        TrailerPreviewsEnabled = enabled;
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
