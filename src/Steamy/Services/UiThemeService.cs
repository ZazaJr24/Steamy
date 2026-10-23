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
    private static ResourceDictionary? _darkPalette, _lightPalette;
    private static ResourceDictionary DarkPalette => _darkPalette ??= new() { Source = new Uri(PalettePackBase + "Dark.xaml") };
    private static ResourceDictionary LightPalette => _lightPalette ??= new() { Source = new Uri(PalettePackBase + "Light.xaml") };

    private static string _currentBackdrop = "None";
    private static bool _isLight;
    internal static bool IsLight => _isLight;
    internal static event EventHandler? PaletteChanged;
    internal static bool IsHighContrastActive { get; private set; }
    internal static bool AmbientEnabled { get; private set; } = true;
    internal static Color BackgroundColor { get; private set; } = Colors.Black;
    internal static Brush BackgroundBrush { get; private set; } = InitialBackground();
    private static Brush InitialBackground() { var brush = new SolidColorBrush(Colors.Black); brush.Freeze(); return brush; }
    internal static bool StaticBackgroundIsLight => Luminance(BackgroundColor) > .179;

    internal static double Luminance(Color color)
    {
        static double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    public static bool TryBackgroundColor(string? text, out Color color)
    {
        color = Colors.Black;
        var hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length == 3) hex = string.Concat(hex.SelectMany(c => new[] { c, c }));
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }
    public static void ConfigureBackground(Steamy.Models.AppSettings settings)
    {
        SetBackground(settings);
        ApplyGlassPolicy();
    }
    private static void SetBackground(Steamy.Models.AppSettings settings)
    {
        AmbientEnabled = settings.AmbientEnabled;
        TryBackgroundColor(settings.BackgroundColor, out var color);
        BackgroundColor = color;
        var brush = new SolidColorBrush(color); brush.Freeze(); BackgroundBrush = brush;
    }

    static UiThemeService() => MotionPreferences.Changed += (_, _) => ApplyBackdrop(_currentBackdrop);

    public static void Apply(string? appearance)
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>()?.Load();
            if (settings is not null) SetBackground(settings);
            MotionPreferences.Configure(settings?.ReduceEffects ?? false);
            MotionPreferences.ConfigureTrailers(settings?.AutoplayTrailers ?? true);
        }
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
        ApplyGlassPolicy();
    }

    public static void ApplyBackdrop(string? style)
    {
        _currentBackdrop = style ?? "None";
        ApplyGlassPolicy();

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
            ? new SolidColorBrush(Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0xEF, 0x00, 0x00, 0x00));
        ApplyGlassPolicy();
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
        }
        // Applying Mica replaces the window's resource reference with a local transparent
        // brush. Restore the reference as well when effects or the backdrop are disabled.
        if (app.MainWindow is FluentWindow window)
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppBackgroundBrush");
        ApplyGlassPolicy();
    }

    internal static void ApplyGlassPolicy(bool? highContrast = null)
    {
        var app = Application.Current;
        if (app is null) return;
        var palette = _isLight ? LightPalette : DarkPalette;
        var contrast = highContrast ?? SystemParameters.HighContrast;
        IsHighContrastActive = contrast;
        var opaque = MotionPreferences.ReduceEffects || contrast;
        foreach (var key in new[] { "SocialBackdropBrush", "SocialChromeBrush", "SocialEdgeBrush", "SocialCanvasBrush", "SocialSidebarBrush", "SocialCardBrush", "SocialFieldBrush", "SocialBorderBrush", "SocialAccentBrush", "SocialAccentSoftBrush", "SocialActionBrush", "SocialOwnTextBrush", "SocialOwnBubbleBrush", "SocialNotificationBrush" })
            app.Resources[key] = palette[key];
        if (opaque)
        {
            foreach (var key in new[] { "SocialBackdropBrush", "SocialChromeBrush", "SocialCanvasBrush", "SocialSidebarBrush", "SocialNotificationBrush" })
                app.Resources[key] = palette["SolidSurfaceBrush"];
            app.Resources["SocialCardBrush"] = app.Resources["SocialFieldBrush"] = palette["SolidBackgroundFillColorSecondaryBrush"];
        }
        if (contrast)
        {
            foreach (var key in new[] { "SocialBackdropBrush", "SocialChromeBrush", "SocialCanvasBrush", "SocialSidebarBrush", "SocialCardBrush", "SocialFieldBrush", "SocialNotificationBrush" })
                app.Resources[key] = SystemColors.WindowBrush;
            app.Resources["SocialEdgeBrush"] = app.Resources["SocialBorderBrush"] = app.Resources["SocialAccentBrush"] = SystemColors.WindowTextBrush;
            app.Resources["SocialOwnBubbleBrush"] = app.Resources["SocialActionBrush"] = SystemColors.HighlightBrush;
            app.Resources["SocialOwnTextBrush"] = SystemColors.HighlightTextBrush;
            app.Resources["SocialAccentSoftBrush"] = SystemColors.WindowBrush;
        }
        if (_isLight)
        {
            var drawing = new DrawingGroup();
            foreach (var key in new[] { "AppBackgroundBrush", "AmbientBlueBrush", "AmbientVioletBrush", "AmbientTealBrush" })
                drawing.Children.Add(new GeometryDrawing((Brush)palette[key], null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            var workspace = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
            workspace.Freeze();
            app.Resources["WorkspaceBackgroundBrush"] = workspace;
        }
        else app.Resources["WorkspaceBackgroundBrush"] = Brushes.Black;
        foreach (var key in new[] { "SidebarBackgroundBrush", "GlassSurfaceBrush", "DialogSurfaceBrush", "GameDialogSurfaceBrush", "SurfaceBrush", "SurfaceElevatedBrush", "SurfaceSubtleBrush", "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "GlassBorderBrush", "BorderSubtleBrush", "AccentBrush", "DownloadAccentBrush", "TextOnAccentBrush", "NavigationViewItemBackgroundSelected", "NavigationViewItemBackgroundPointerOver", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorInputActiveBrush", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush" })
            app.Resources[key] = palette[key];
        app.Resources["PageBackgroundBrush"] = Brushes.Transparent;
        app.Resources["SidebarSelectedForegroundBrush"] = palette["TextPrimaryBrush"];
        app.Resources["LauncherBackgroundBrush"] = contrast ? SystemColors.WindowBrush : palette["LauncherBackgroundBrush"];
        app.Resources["LauncherForegroundBrush"] = contrast ? SystemColors.WindowTextBrush : palette["LauncherForegroundBrush"];
        app.Resources["ShellChromeSheenBrush"] = contrast || opaque ? Brushes.Transparent : palette["ShellChromeSheenBrush"];
        app.Resources["ShellChromeBorderBrush"] = contrast ? SystemColors.WindowTextBrush : palette["ShellChromeBorderBrush"];
        foreach (var key in new[] { "HeroForegroundBrush", "HeroSecondaryForegroundBrush", "HeroControlBackgroundBrush", "HeroControlBorderBrush", "HeroShadeOpacity" })
            app.Resources[key] = palette[key];
        app.Resources["GameDetailsBackgroundBrush"] = palette["GameDetailsBackgroundBrush"];
        app.Resources["GameDetailsGradientBrush"] = palette["GameDetailsGradientBrush"];
        if (opaque)
        {
            app.Resources["SidebarBackgroundBrush"] = palette["SolidSurfaceBrush"];
            app.Resources["GlassSurfaceBrush"] = palette["SolidSurfaceBrush"];
            app.Resources["DialogSurfaceBrush"] = palette["SolidSurfaceBrush"];
            app.Resources["GameDialogSurfaceBrush"] = palette["SolidSurfaceBrush"];
            app.Resources["SurfaceSubtleBrush"] = palette["SolidBackgroundFillColorSecondaryBrush"];
            foreach (var key in new[] { "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorInputActiveBrush", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush" }) app.Resources[key] = palette["SolidSurfaceBrush"];
            app.Resources["SurfaceBrush"] = palette["SolidSurfaceBrush"];
            app.Resources["SurfaceElevatedBrush"] = palette["SolidSurfaceBrush"];
        }
        if (contrast)
        {
            foreach (var key in new[] { "WorkspaceBackgroundBrush", "SidebarBackgroundBrush", "GlassSurfaceBrush", "DialogSurfaceBrush", "GameDialogSurfaceBrush", "SurfaceBrush", "SurfaceElevatedBrush", "SurfaceSubtleBrush", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorInputActiveBrush", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush" })
                app.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "GlassBorderBrush", "BorderSubtleBrush" })
                app.Resources[key] = SystemColors.WindowTextBrush;
            app.Resources["AccentBrush"] = app.Resources["NavigationViewItemBackgroundSelected"] = SystemColors.HighlightBrush;
            app.Resources["DownloadAccentBrush"] = SystemColors.HighlightBrush;
            app.Resources["TextOnAccentBrush"] = SystemColors.HighlightTextBrush;
            app.Resources["SidebarSelectedForegroundBrush"] = SystemColors.HighlightTextBrush;
            app.Resources["HeroForegroundBrush"] = app.Resources["HeroSecondaryForegroundBrush"] = SystemColors.WindowTextBrush;
            app.Resources["HeroControlBackgroundBrush"] = SystemColors.WindowBrush;
            app.Resources["HeroControlBorderBrush"] = SystemColors.WindowTextBrush;
            app.Resources["HeroShadeOpacity"] = 0d;
            app.Resources["GameDetailsBackgroundBrush"] = SystemColors.WindowBrush;
            app.Resources["GameDetailsGradientBrush"] = Brushes.Transparent;
        }
        else if (!AmbientEnabled)
        {
            app.Resources["WorkspaceBackgroundBrush"] = app.Resources["AppBackgroundBrush"] = BackgroundBrush;
            app.Resources["GameDetailsBackgroundBrush"] = BackgroundBrush;
            app.Resources["GameDetailsGradientBrush"] = Brushes.Transparent;
        }
        if (app.MainWindow is MainWindow mainWindow) mainWindow.RefreshCaptionAppearance(paletteChanged: true);
        PaletteChanged?.Invoke(null, EventArgs.Empty);
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
