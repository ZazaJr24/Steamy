using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;

namespace Steamy.Services;

public static class UiThemeService
{
    private const string PalettePackBase = "pack://application:,,,/Steamy;component/Resources/Themes/";
    private static readonly ResourceDictionary DarkPalette = new() { Source = new Uri(PalettePackBase + "Dark.xaml") };
    private static readonly ResourceDictionary LightPalette = new() { Source = new Uri(PalettePackBase + "Light.xaml") };

    private static string _currentBackdrop = "None";
    private static bool _isLight;

    static UiThemeService() => MotionPreferences.Changed += (_, _) => ApplyBackdrop(_currentBackdrop);

    public static void Apply(string? appearance)
    {
        try { MotionPreferences.Configure(App.Services?.GetService<ISettingsService>()?.Load().ReduceEffects ?? false); }
        catch { }
        var light = string.Equals(appearance, "System", StringComparison.OrdinalIgnoreCase)
            ? IsSystemUsingLightApps()
            : string.Equals(appearance, "Light", StringComparison.OrdinalIgnoreCase);

        _isLight = light;
        try { SwapPalette(light); } catch { }

        try
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark, updateAccent: false);
            ApplyAccent(light);
        }
        catch { }

        if (_currentBackdrop != "None" && MotionPreferences.BackdropBlurEnabled)
            ApplyTransparentBackgrounds();
        else
            RestoreOpaqueBackgrounds();
    }

    public static void ApplyBackdrop(string? style)
    {
        _currentBackdrop = style ?? "None";

        try
        {
            var window = Application.Current?.MainWindow as FluentWindow;
            if (window is null) return;

            var backdropType = (MotionPreferences.BackdropBlurEnabled ? style?.ToLowerInvariant() : "none") switch
            {
                "mica" => WindowBackdropType.Mica,
                "micaalt" or "mica alt" => WindowBackdropType.Tabbed,
                "acrylic" => WindowBackdropType.Acrylic,
                "tabbed" => WindowBackdropType.Tabbed,
                _ => WindowBackdropType.None
            };

            window.WindowBackdropType = backdropType;

            if (backdropType != WindowBackdropType.None)
            {
                window.Background = Brushes.Transparent;
                ApplyTransparentBackgrounds();
            }
            else
            {
                RestoreOpaqueBackgrounds();
            }
        }
        catch { }
    }

    private static void ApplyTransparentBackgrounds()
    {
        var app = Application.Current;
        if (app is null) return;

        app.Resources["AppBackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
        // A light tint in the light theme; the old fixed dark tint turned the sidebar grey there.
        app.Resources["SidebarBackgroundBrush"] = _isLight
            ? new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x40, 0x09, 0x0B, 0x10));
    }

    private static void RestoreOpaqueBackgrounds()
    {
        var app = Application.Current;
        if (app is null) return;

        var palette = app.Resources.MergedDictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString;
            if (source is null) return false;
            return source.Contains("Resources/Themes/", StringComparison.OrdinalIgnoreCase)
                || source.Contains(@"Resources\Themes\", StringComparison.OrdinalIgnoreCase);
        });

        if (palette is not null)
        {
            if (palette.Contains("AppBackgroundBrush"))
                app.Resources["AppBackgroundBrush"] = palette["AppBackgroundBrush"];
            if (palette.Contains("SidebarBackgroundBrush"))
                app.Resources["SidebarBackgroundBrush"] = palette["SidebarBackgroundBrush"];
        }
        // Applying Mica replaces the window's resource reference with a local transparent
        // brush. Restore the reference as well when effects or the backdrop are disabled.
        if (app.MainWindow is FluentWindow window)
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppBackgroundBrush");
    }

    // Brand blue for every Fluent accent surface (primary buttons, toggles, check boxes, focus
    // rings). WPF-UI lightens it a little in the dark theme, so both themes get the same hue.
    private static void ApplyAccent(bool light)
    {
        var accent = light ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0x2F, 0x6F, 0xEB);
        ApplicationAccentColorManager.Apply(accent, light ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }

    private static bool IsSystemUsingLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SwapPalette(bool light)
    {
        var app = Application.Current;
        if (app is null) return;

        var target = light ? LightPalette : DarkPalette;
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString;
            if (source is null) return false;
            return source.Contains("Resources/Themes/", StringComparison.OrdinalIgnoreCase)
                || source.Contains(@"Resources\Themes\", StringComparison.OrdinalIgnoreCase);
        });

        if (existing is not null && !ReferenceEquals(existing, target))
        {
            dictionaries[dictionaries.IndexOf(existing)] = target;
        }
        else if (existing is null)
        {
            dictionaries.Add(target);
        }
    }
}
