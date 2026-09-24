using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SteamContentManager.Services;

public static class UiThemeService
{
    private const string PalettePackBase = "pack://application:,,,/ResonanceTools;component/Resources/Themes/";
    private static readonly ResourceDictionary DarkPalette = new() { Source = new Uri(PalettePackBase + "Dark.xaml") };
    private static readonly ResourceDictionary LightPalette = new() { Source = new Uri(PalettePackBase + "Light.xaml") };

    private static string _currentBackdrop = "None";

    public static void Apply(string? appearance)
    {
        var light = string.Equals(appearance, "System", StringComparison.OrdinalIgnoreCase)
            ? IsSystemUsingLightApps()
            : string.Equals(appearance, "Light", StringComparison.OrdinalIgnoreCase);

        try { SwapPalette(light); } catch { }

        try
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark, updateAccent: false);
            ApplyAccent(light);
        }
        catch { }

        if (_currentBackdrop != "None")
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

            var backdropType = style?.ToLowerInvariant() switch
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
        app.Resources["SidebarBackgroundBrush"] = new SolidColorBrush(Color.FromArgb(0x40, 0x0A, 0x0B, 0x0E));
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
    }

    private static void ApplyAccent(bool light)
    {
        var accent = light ? Color.FromRgb(0x5C, 0x6B, 0x84) : Color.FromRgb(0x8C, 0x99, 0xAE);
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
