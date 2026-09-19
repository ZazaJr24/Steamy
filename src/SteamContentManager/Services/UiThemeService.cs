using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace SteamContentManager.Services;

/// <summary>
/// Applies the WPF-UI Fluent theme and swaps the app's own brush palette so all
/// custom DynamicResource brushes follow the selected appearance at runtime.
/// </summary>
public static class UiThemeService
{
    // Qualify the pack URI with the assembly name so the palette resolves the same way whether the
    // app is started normally or hosted (e.g. by the test harness). Without the ";component" the
    // bare "pack://application:,,,/…" URI can fail to load, which silently left the palette on dark.
    private const string PalettePackBase = "pack://application:,,,/SteamContentManager;component/Resources/Themes/";
    private static readonly ResourceDictionary DarkPalette = new() { Source = new Uri(PalettePackBase + "Dark.xaml") };
    private static readonly ResourceDictionary LightPalette = new() { Source = new Uri(PalettePackBase + "Light.xaml") };

    public static void Apply(string? appearance)
    {
        var light = string.Equals(appearance, "System", StringComparison.OrdinalIgnoreCase)
            ? IsSystemUsingLightApps()
            : string.Equals(appearance, "Light", StringComparison.OrdinalIgnoreCase);

        // Swap the app's own brush palette first and on its own: this is what every custom
        // DynamicResource brush follows, so it must happen even if the Fluent theme call below
        // fails for any reason (e.g. no active FluentWindow).
        try { SwapPalette(light); } catch { }

        try
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark, updateAccent: false);
            ApplyAccent(light);
        }
        catch
        {
            // The Fluent theme/accent is optional and must never crash the app.
        }
    }

    /// <summary>
    /// Keeps the Fluent accent (primary buttons, toggles, selection) in step with the app's own
    /// AccentBrush instead of the Windows accent colour, so both palettes read as one design.
    /// </summary>
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
        // IMPORTANT: match ONLY the app's own palette dictionaries (Resources/Themes).
        // A broad "/Themes/" check also matches WPF-UI's own ThemesDictionary registered in
        // App.xaml, which would replace the whole Fluent theme and break every control color.
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
