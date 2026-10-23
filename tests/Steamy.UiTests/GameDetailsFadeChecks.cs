using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Controls;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private static void CheckDetailsFadeAndStickyBack(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        var baseline = Environment.GetEnvironmentVariable("STEAMY_UI_DETAILS_FADE_BASELINE") == "1";
        var output = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(output);
        var game = new Game { AppId = 2050650, Name = "Resident Evil 4", CoverColor = "#242425", InstallState = GameInstallState.NotInstalled };
        var art = artwork.LoadAsync(game); PumpUntil(() => art.IsCompleted); art.GetAwaiter().GetResult();
        Assert.NotNull(game.ArtworkImage);
        store.Games.Add(game);
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            WindowState = WindowState.Normal, Width = 1280, Height = 900 };
        var previous = Application.Current.MainWindow;
        Application.Current.MainWindow = window;
        var measures = new List<object>();
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(500));
            var model = provider.GetRequiredService<GameDetailsViewModel>();
            model.SelectGame(game);
            provider.GetRequiredService<INavigationService>().Navigate<GameDetailsPage>();
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.HeroArtwork is not null && model.CanEditPreferences);
            var page = Descendants<GameDetailsPage>(window).Single();
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme); window.UpdateLayout();
                PumpUntil(() => ReferenceEquals(page.AppliedAmbientArtwork, model.HeroArtwork));
                // Repeatable header fixture; no live store metadata/network in render comparisons.
                typeof(GameDetailsViewModel).GetProperty(nameof(model.ReleaseDate))!.SetValue(model, "Mar 23, 2023");
                typeof(GameDetailsViewModel).GetProperty(nameof(model.Genres))!.SetValue(model, "Action · Survival Horror");
                var scroll = (ScrollViewer)page.FindName("DetailsScroll");
                var back = (Button)page.FindName("BackButton");
                scroll.ScrollToTop(); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Save("details-" + theme.ToLowerInvariant() + "-top.png");
                var before = back.TranslatePoint(new Point(), page);
                scroll.ScrollToVerticalOffset(Math.Min(650, scroll.ScrollableHeight));
                window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(250));
                var after = back.TranslatePoint(new Point(), page);
                Assert.True(scroll.VerticalOffset > 100);
                if (!baseline)
                {
                    Assert.Equal(before.Y, after.Y, 3);
                    Assert.True(back.IsVisible && back.IsEnabled);
                    Assert.InRange(after.Y, 64, 120);
                    Assert.InRange(after.X, 8, 40);
                    Assert.Null(((FrameworkElement)page.FindName("HeroArtworkFade")).OpacityMask);
                }
                Save("details-" + theme.ToLowerInvariant() + "-scrolled.png");
                scroll.ScrollToTop(); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(250));
                for (var warmup = 0; warmup < 3; warmup++) Render();
                var samples = new List<double>();
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                for (var run = 0; run < 20; run++)
                {
                    var started = Stopwatch.GetTimestamp(); Render();
                    samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                object? captionSampling = baseline ? null : CompareCaptionSampling(page);
                measures.Add(new { theme, baseline, backBeforeY = before.Y, backAfterY = after.Y, captionSampling,
                    renderTimesMs = samples, medianRenderMs = samples.Order().ElementAt(samples.Count / 2),
                    cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds });
            }
            Assert.NotEqual(new WindowInteropHelper(window).Handle, GetForegroundWindow());
            File.WriteAllText(Path.Combine(output, "details-fade-render.json"), JsonSerializer.Serialize(new {
                scope = "20 warmed RenderTargetBitmap snapshots of the real offscreen game page per theme, fixed artwork and fixture metadata; not interactive FPS or whole-app speed.",
                baseline, foregroundTaken = false, viewport = "1280x900", measures
            }, new JsonSerializerOptions { WriteIndented = true }));
            void Render()
            {
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
            }
            void Save(string name)
            {
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
            }
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
    }

    private static object CompareCaptionSampling(GameDetailsPage page)
    {
        var legacyVisual = new DrawingVisual();
        var legacyBitmap = new RenderTargetBitmap(160, 12, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[160 * 12 * 4];
        void LegacyCapture()
        {
            // Exact previous algorithm from ChromeSurfaceSample.Capture, on this same page.
            using (var draw = legacyVisual.RenderOpen())
                draw.DrawRectangle(new VisualBrush(page) { AutoLayoutContent = false,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, page.ActualWidth, 64), Stretch = Stretch.Fill },
                    null, new Rect(0, 0, 160, 12));
            legacyBitmap.Clear(); legacyBitmap.Render(legacyVisual); legacyBitmap.CopyPixels(pixels, 160 * 4, 0);
            using var clear = legacyVisual.RenderOpen();
        }
        var prepared = new ChromeSurfaceSample(page);
        LegacyCapture(); prepared.Capture(page);
        var legacy = new List<double>(); var current = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var start = Stopwatch.GetTimestamp();
            LegacyCapture(); legacy.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = Stopwatch.GetTimestamp();
            prepared.Capture(page); current.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return new { scope = "Old whole-page VisualBrush capture versus prepared background capture, same page, alternating 20 samples; caption subsystem only.",
            legacyMedianMs = legacy.Order().ElementAt(10), preparedMedianMs = current.Order().ElementAt(10),
            legacyTimesMs = legacy, preparedTimesMs = current };
    }
}
