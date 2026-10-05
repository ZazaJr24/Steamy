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
        ApplyWorkspaceBackground(light);

        try
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark, updateAccent: false);
            ApplyAccent(light);
        }
        catch { }

        if (_currentBackdrop != "None" && MotionPreferences.NativeBackdropEnabled)
            ApplyTransparentBackgrounds();
        else
            RestoreOpaqueBackgrounds();

        if (Application.Current?.MainWindow is MainWindow mainWindow)
            mainWindow.RefreshFrameAppearance();
    }

    private static void ApplyWorkspaceBackground(bool light)
    {
        var app = Application.Current;
        if (app is null) return;

        // Keep the workspace a solid palette colour in both themes. Translucent ambient
        // gradients made the dark workspace look grey, especially with Mica enabled.
        app.Resources["WorkspaceBackgroundBrush"] = (light ? LightPalette : DarkPalette)["AppBackgroundBrush"];
    }

    public static void ApplyBackdrop(string? style)
    {
        _currentBackdrop = style ?? "None";

        try
        {
            var window = Application.Current?.MainWindow as FluentWindow;
            if (window is null) return;

            var backdropType = (MotionPreferences.NativeBackdropEnabled ? style?.ToLowerInvariant() : "none") switch
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

        app.Resources["AppBackgroundBrush"] = _isLight
            ? new SolidColorBrush(Color.FromArgb(0xEA, 0xF4, 0xF6, 0xFA))
            : new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
        // Keep the palette's translucent sidebar gradient when native backdrops are enabled.
        app.Resources["SidebarBackgroundBrush"] = (_isLight ? LightPalette : DarkPalette)["SidebarBackgroundBrush"];
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
        var accent = light ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0xC3, 0xC5, 0xCB);
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
