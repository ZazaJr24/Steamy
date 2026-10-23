using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckPageFlowsOffscreen(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(folder);
        var game = new Game { AppId = 1808500, Name = "ARC Raiders", CoverColor = "#262044", InstallState = GameInstallState.Installed };
        store.Games.Add(game);
        var loading = artwork.LoadAsync(game); PumpUntil(() => loading.IsCompleted); loading.GetAwaiter().GetResult();
        var model = services.GetRequiredService<GameDetailsViewModel>();
        model.SelectGame(game);
        var activate = model.ActivateAsync(); PumpUntil(() => activate.IsCompleted); activate.GetAwaiter().GetResult();
        var reduced = MotionPreferences.ReduceEffects;
        MotionPreferences.Configure(false);
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var width in new[] { 980, 1440, 1920 })
                {
                    var page = new GameDetailsPage();
                    LayoutFlow(page, width);
                    CheckGameAmbientInvalidation(page, model);
                    // No presentation source is created. Reuse the production ambient
                    // renderer explicitly, since a detached Page never receives Loaded.
                    if (model.HeroArtwork is { } hero)
                    {
                        Brush ambient;
                        if (theme == "Light") ambient = PaperAmbient.Create(ArtworkPalette.FromImage(hero));
                        else
                        {
                            var task = GameDetailsAmbient.ForAsync(hero, width / ((Grid)page.FindName("GameHero")).Height, CancellationToken.None);
                            PumpUntil(() => task.IsCompleted);
                            ambient = new ImageBrush(task.GetAwaiter().GetResult()) { Stretch = Stretch.Fill };
                        }
                        ((Border)page.FindName("GameDetailsWarmGradient")).Background = ambient;
                    }
                    SaveFlow(page, folder, $"game-{theme}-{width}", width);
                    var identity = (Grid)page.FindName("GameIdentityLayout");
                    var body = (StackPanel)page.FindName("DetailsBody");
                    Assert.InRange(identity.ActualWidth, 0, 880);
                    Assert.InRange(Math.Abs(identity.TranslatePoint(new Point(identity.ActualWidth / 2, 0), page).X - width / 2d), 0, 1);
                    var actions = (WrapPanel)page.FindName("GameIdentityActions");
                    Assert.True(actions.ActualHeight <= 95, $"Actions should stay compact at {width}px.");
                    var headings = Descendants<TextBlock>(page).Where(text => text.Text is "Game details" or "Preview").ToArray();
                    Assert.Equal(new[] { "Game details", "Preview" }, headings.Select(text => text.Text));
                    Assert.True(((Button)page.FindName("AddToSteamButton")).Command!.CanExecute(null));

                    var controller = new ControllersPage();
                    LayoutFlow(controller, width);
                    Assert.Equal(Visibility.Visible, ((StackPanel)controller.FindName("ConnectPanel")).Visibility);
                    for (var step = 0; step < 3; step++)
                    {
                        var radio = (RadioButton)controller.FindName(new[] { "ConnectStep", "TestStep", "CustomizeStep" }[step]);
                        radio.IsChecked = true;
                        LayoutFlow(controller, width);
                        var panels = new[] { "ConnectPanel", "TestPanel", "CustomizePanel" }.Select(name => (StackPanel)controller.FindName(name)).ToArray();
                        Assert.Single(panels, panel => panel.Visibility == Visibility.Visible);
                        Assert.Equal(Visibility.Visible, panels[step].Visibility);
                        SaveFlow(controller, folder, $"controller-{theme}-{width}-{step}", width);
                    }
                    Assert.False(((Expander)controller.FindName("ControllerSetupExpander")).IsExpanded);
                    Assert.IsType<ComboBox>(controller.FindName("SelectedMappingOutput"));
                }
            }
            var home = new DashboardPage();
            LayoutFlow(home, 1440);
            Assert.True(SmoothScroll.GetEnabled((ScrollViewer)home.FindName("RecentScroll")));
            Assert.True(SmoothScroll.GetHorizontal((ScrollViewer)home.FindName("RecentScroll")));
            SaveFlow(home, folder, "dashboard-layout", 1440);
            Assert.Equal(0, ((GameDetailsFixture)services.GetRequiredService<IGameDetailsDataService>()).Launches);
            Assert.Empty(Application.Current.Windows.Cast<Window>());
        }
        finally
        {
            MotionPreferences.Configure(reduced);
            var deactivate = model.DeactivateAsync(); PumpUntil(() => deactivate.IsCompleted); deactivate.GetAwaiter().GetResult();
            services.GetRequiredService<DashboardViewModel>().StopLiveStats();
        }
    }

    private static void LayoutFlow(Page page, int width)
    {
        page.Measure(new Size(width, 800)); page.Arrange(new Rect(0, 0, width, 800)); page.UpdateLayout();
        PumpDispatcher(TimeSpan.FromMilliseconds(60)); page.UpdateLayout();
    }

    private static void CheckGameAmbientInvalidation(GameDetailsPage page, GameDetailsViewModel model)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var refresh = typeof(GameDetailsPage).GetMethod("UpdateBackdrop", flags)!;
        var changed = typeof(GameDetailsPage).GetMethod("AmbientChanged", flags)!;
        var cancel = typeof(GameDetailsPage).GetMethod("CancelAmbient", flags)!;
        var pending = typeof(GameDetailsPage).GetField("_ambientRender", flags)!;
        var artwork = typeof(GameDetailsViewModel).GetProperty(nameof(model.HeroArtwork))!;
        var saved = model.HeroArtwork;
        try
        {
            refresh.Invoke(page, null);
            var first = Assert.IsType<CancellationTokenSource>(pending.GetValue(page));
            var token = first.Token;
            var tint = page.Resources["TextSecondaryBrush"];
            refresh.Invoke(page, null);
            Assert.Same(first, pending.GetValue(page));
            Assert.Same(tint, page.Resources["TextSecondaryBrush"]);
            Assert.False(token.IsCancellationRequested);

            changed.Invoke(page, new object?[] { null, EventArgs.Empty });
            Assert.True(token.IsCancellationRequested);
            var themed = Assert.IsType<CancellationTokenSource>(pending.GetValue(page));
            var themedToken = themed.Token;
            Assert.NotSame(first, themed);

            var replacement = new DrawingImage(new GeometryDrawing(Brushes.Teal, null, new RectangleGeometry(new Rect(0, 0, 1200, 400))));
            replacement.Freeze(); artwork.SetValue(model, replacement);
            refresh.Invoke(page, null);
            Assert.True(themedToken.IsCancellationRequested);
            var latest = Assert.IsType<CancellationTokenSource>(pending.GetValue(page));
            var latestToken = latest.Token;
            refresh.Invoke(page, null);
            Assert.Same(latest, pending.GetValue(page));
            cancel.Invoke(page, null);
            Assert.True(latestToken.IsCancellationRequested);
            Assert.Null(pending.GetValue(page));

            // A re-entered page must request the same image again after cancellation.
            refresh.Invoke(page, null);
            Assert.NotSame(latest, Assert.IsType<CancellationTokenSource>(pending.GetValue(page)));
        }
        finally
        {
            artwork.SetValue(model, saved);
            cancel.Invoke(page, null);
        }
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    }

    private static void SaveFlow(Page page, string folder, string name, int width)
    {
        var image = new RenderTargetBitmap(width, 800, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(UiThemeService.IsLight ? Brushes.White : Brushes.Black, null, new Rect(0, 0, width, 800));
        image.Render(background); image.Render(page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
