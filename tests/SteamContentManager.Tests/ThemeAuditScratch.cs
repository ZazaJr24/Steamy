using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamContentManager.Pages;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class ThemeAuditScratch
{
    [Fact]
    public void RunAudit()
    {
        var report = new StringBuilder();
        var appBackground = new Dictionary<string, Color>(StringComparer.Ordinal);
        WpfTestHost.Run(() =>
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                try
                {
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                        theme == "Light" ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark);
                    report.AppendLine($"[theme {theme}] ApplicationThemeManager.Apply: ok");
                }
                catch (Exception exception)
                {
                    report.AppendLine($"[theme {theme}] ApplicationThemeManager.Apply threw {exception.GetType().Name}: {exception.Message}");
                }

                UiThemeService.Apply(theme);
                WpfTestHost.Pump();
                if (Application.Current.Resources["AppBackgroundBrush"] is SolidColorBrush appBrush)
                    appBackground[theme] = appBrush.Color;
                report.AppendLine($"===== {theme} =====");
                report.AppendLine($"palette text={Brush("TextPrimaryBrush")} surface={Brush("SurfaceBrush")} app={Brush("AppBackgroundBrush")}");

                var page = new SettingsPage { Width = 1000 };
                page.Measure(new Size(1000, 4000));
                page.Arrange(new Rect(0, 0, 1000, 4000));
                page.UpdateLayout();

                AppendAudit(page, report);
                report.AppendLine();
            }
        });

        var auditPath = Path.Combine(AppContext.BaseDirectory, "theme-audit.txt");
        File.WriteAllText(auditPath, report.ToString());

        Assert.NotEmpty(report.ToString());
        Assert.Contains("===== Dark =====", report.ToString(), StringComparison.Ordinal);
        Assert.Contains("===== Light =====", report.ToString(), StringComparison.Ordinal);

        // The app's own brush palette must actually follow the selected appearance. Regression guard
        // for the light theme not applying (a bad pack URI once left every appearance on dark).
        Assert.Equal(Color.FromRgb(0x0E, 0x0F, 0x12), appBackground["Dark"]);
        Assert.Equal(Color.FromRgb(0xEE, 0xF0, 0xF3), appBackground["Light"]);
    }

    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly List<string> LowContrast = new();

    private static void AppendAudit(DependencyObject root, StringBuilder report)
    {
        Counts.Clear();
        LowContrast.Clear();
        Walk(root, null);

        foreach (var entry in Counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"  {entry.Value,3}x {entry.Key}");
        }

        report.AppendLine("  --- low contrast ---");
        foreach (var line in LowContrast.Distinct()) report.AppendLine(line);
    }

    private static void Walk(DependencyObject node, Brush? inheritedBackground)
    {
        var background = inheritedBackground;
        var foreground = (Brush?)null;

        switch (node)
        {
            case Border border when border.Background is not null:
                background = border.Background;
                break;
            case Panel panel when panel.Background is not null:
                background = panel.Background;
                break;
            case Control control when control.Background is not null && node is not TextBlock:
                background = control.Background;
                break;
        }

        if (node is Control element)
        {
            foreground = element.Foreground;
            var key = $"{node.GetType().Name} bg={Describe(background)} fg={Describe(foreground)}";
            Counts[key] = Counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        if (node is TextBlock text && text.Text.Length > 0)
        {
            var fg = text.Foreground is SolidColorBrush solid ? solid.Color : (Color?)null;
            var bg = background is SolidColorBrush back ? back.Color : (Color?)null;
            if (fg is not null && bg is not null)
            {
                var ratio = Contrast(fg.Value, bg.Value);
                if (ratio < 3.0 && text.Text.Trim().Length > 2)
                {
                    LowContrast.Add($"  LOW {ratio:0.0}:1  \"{Trim(text.Text)}\" fg={Solid(fg.Value)} bg={Solid(bg.Value)}");
                }
            }
        }

        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
        {
            Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, index), background);
        }
    }

    private static string Trim(string value) => value.Length > 42 ? value[..42] + "…" : value;

    private static string Brush(string key) =>
        Application.Current.TryFindResource(key) is SolidColorBrush brush ? Solid(brush.Color) : "MISSING";

    private static string Solid(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string Describe(Brush? brush) => brush switch
    {
        SolidColorBrush solid => Solid(solid.Color),
        null => "none",
        _ => brush.GetType().Name
    };

    private static double Contrast(Color a, Color b)
    {
        double La = Luminance(a), Lb = Luminance(b);
        var high = Math.Max(La, Lb);
        var low = Math.Min(La, Lb);
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
