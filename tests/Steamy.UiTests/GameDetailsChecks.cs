using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Playtime;
using Steamy.Services;
using Steamy.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckGameDetails(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        var arc = new Game { AppId = 1808500, Name = "ARC Raiders", ShortName = "ARC", CoverColor = "#262044", InstallState = GameInstallState.Installed, InstallFolder = @"C:\Games\ARC Raiders" };
        var local = new Game { AppId = -42, Name = "Local installation", ShortName = "LOCAL", IsLocalDownload = true, InstallationSource = "PC scan", InstallState = GameInstallState.Installed, InstallFolder = @"C:\Games\Local" };
        store.Games.Add(arc); store.Games.Add(local);
        AddScreenshotLibrary(store, artwork);
        var art = artwork.LoadAsync(arc); PumpUntil(() => art.IsCompleted); art.GetAwaiter().GetResult();
        var model = provider.GetRequiredService<GameDetailsViewModel>();
        var fixture = (GameDetailsFixture)provider.GetRequiredService<IGameDetailsDataService>();
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800, ShowInTaskbar = false };
        Application.Current.MainWindow = window; window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        try
        {
            var dashboard = Descendants<DashboardPage>(window).Single();
            var dashboardModel = (DashboardViewModel)dashboard.DataContext;
            var tile = Descendants<Button>(dashboard).First(button => button.CommandParameter is Game game && game.AppId == arc.AppId && button.Command == dashboardModel.OpenGameCommand);
            Assert.True(tile.Command!.CanExecute(arc)); tile.Command.Execute(arc);
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences && model.AchievementsAvailable);
            var page = Descendants<GameDetailsPage>(window).Single();
            window.UpdateLayout();
            Assert.Equal("ARC Raiders", model.Name);
            Assert.Equal("2h 05m", model.PlayTimeLabel);
            Assert.Equal("2 / 3 unlocked", model.AchievementSummary);
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            Assert.InRange(page.TranslatePoint(new Point(), window).Y, 0, 1);
            Assert.Same(model.HeroArtwork, ((Image)page.FindName("HeroImage")).Source);
            Assert.Equal(0, fixture.Launches);
            var reduced = MotionPreferences.ReduceEffects;
            MotionPreferences.Configure(true); model.SelectedTab = 2; window.UpdateLayout();
            Assert.Equal(1, ((FrameworkElement)page.FindName("NotesPanel")).Opacity);
            MotionPreferences.Configure(reduced); model.SelectedTab = 0; window.UpdateLayout();
            Assert.True(((Button)page.FindName("PlayButton")).IsEnabled);
            SaveGameDetailsVisual(window, "overview");
            var cachedPalette = ArtworkPalette.FromImage(model.HeroArtwork);
            Assert.Same(cachedPalette, ArtworkPalette.FromImage(model.HeroArtwork));
            UiThemeService.ApplyGlassPolicy(true);
            Assert.Same(SystemColors.WindowBrush, ((Grid)page.FindName("DetailsScene")).Background);
            Assert.Same(Brushes.Transparent, ((Border)page.FindName("GameDetailsWarmGradient")).Background);
            UiThemeService.ApplyGlassPolicy(false);
            Assert.IsType<DrawingBrush>(((Border)page.FindName("GameDetailsWarmGradient")).Background);
            var scroll = (ScrollViewer)page.FindName("DetailsScroll");
            Assert.False(ScrollViewer.GetCanContentScroll(page));
            Assert.True(scroll.ScrollableHeight > 300);
            Assert.False(scroll.CanContentScroll);
            WheelOver((Image)page.FindName("HeroImage"), -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(scroll.VerticalOffset > 0, $"Wheel offset={scroll.VerticalOffset}, enabled={SmoothScroll.GetEnabled(scroll)}, modifiers={System.Windows.Input.Keyboard.Modifiers}, lines={SystemParameters.WheelScrollLines}");
            Assert.NotEmpty(model.FullDescription); Assert.NotEmpty(model.SystemRequirements); Assert.NotEmpty(model.News);
            if (model.Screenshots.Count > 0)
            {
                Assert.Equal(3, ((ItemsControl)page.FindName("ScreenshotGallery")).Items.Count);
                scroll.ScrollToVerticalOffset(680); window.UpdateLayout();
                SaveGameDetailsVisual(window, "gallery");
            }
            scroll.ScrollToEnd(); window.UpdateLayout();
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight);
            SaveGameDetailsVisual(window, "information");
            scroll.ScrollToTop(); window.UpdateLayout();

            var launch = ((IAsyncRelayCommand)model.PlayCommand).ExecuteAsync(null); PumpUntil(() => launch.IsCompleted); launch.GetAwaiter().GetResult();
            Assert.Equal(1, fixture.Launches); Assert.Contains("requested", model.LaunchStatus);
            model.SelectedTab = 2; window.UpdateLayout();
            var notes = (TextBox)page.FindName("GameNotes");
            Assert.True(notes.IsVisible); Assert.True(notes.IsEnabled);
            notes.Text = "Remember the north extraction\nBring a shield";
            notes.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.SelectedTab = 3; window.UpdateLayout();
            Assert.True(((FrameworkElement)page.FindName("LaunchOptionsPanel")).IsVisible);
            model.LaunchArguments = "-windowed";
            var save = model.FlushAsync(); PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult();
            Assert.Equal("-windowed", fixture.Preferences[arc.AppId].LaunchArguments);
            Assert.Contains("north extraction", fixture.Preferences[arc.AppId].Notes);
            model.SelectedTab = 1; window.UpdateLayout();
            Assert.Equal(3, ((ListBox)page.FindName("AchievementList")).Items.Count);
            SaveGameDetailsVisual(window, "achievements");

            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var size in new[] { new Size(980, 620), new Size(1280, 800) })
                {
                    window.Width = size.Width; window.Height = size.Height; model.SelectedTab = 0;
                    ((ScrollViewer)page.FindName("DetailsScroll")).ScrollToTop(); window.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(25)); window.UpdateLayout();
                    var actions = (FrameworkElement)page.FindName("GameActionBar");
                    Assert.True(actions.ActualWidth <= page.ActualWidth - 60);
                    var play = (FrameworkElement)page.FindName("PlayButton");
                    Assert.True(play.ActualWidth >= 140);
                    Assert.Equal(Math.Clamp(window.ActualHeight * .46, 300, 440), ((FrameworkElement)page.FindName("GameHero")).ActualHeight, 0);
                    Assert.DoesNotContain(Descendants<TextBlock>(actions), text => text.ActualWidth == 0);
                }
            }
            model.SelectedTab = 2; window.UpdateLayout();
            SaveGameDetailsVisual(window, "notes-light");
            model.BackCommand.Execute(null); PumpUntil(() => Descendants<DashboardPage>(window).Any());
            dashboardModel.OpenGameCommand.Execute(local);
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences && !model.AchievementsBusy);
            Assert.Equal("Local installation", model.Name); Assert.Equal("17m", model.PlayTimeLabel);
            Assert.Empty(model.Notes); Assert.Empty(model.Achievements); Assert.False(model.AchievementsAvailable);
            Assert.Null(model.LogoArtwork);
            model.SelectedTab = 3; window.UpdateLayout();
            Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "Choose executable") && button.IsVisible);
            model.Notes = "Local notes";
            model.SelectGame(arc); var activate = model.ActivateAsync(); PumpUntil(() => activate.IsCompleted); activate.GetAwaiter().GetResult();
            Assert.Contains("north extraction", model.Notes); Assert.Equal("Local notes", fixture.Preferences[local.AppId].Notes);
            var navigation = provider.GetRequiredService<INavigationService>();
            navigation.Navigate<LibraryPage>(); PumpUntil(() => Descendants<LibraryPage>(window).Any());
            var library = Descendants<LibraryPage>(window).Single();
            typeof(LibraryPage).GetMethod("GameCard_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(library, [new Button { Tag = local }, new RoutedEventArgs()]);
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences);
            Assert.Same(local, model.Game);
            var currentPage = Descendants<GameDetailsPage>(window).Single();
            currentPage.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => Descendants<LibraryPage>(window).Any());
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            navigation.Navigate<SettingsPage>(); PumpUntil(() => Descendants<SettingsPage>(window).Any());
            var settingsPage = Descendants<SettingsPage>(window).Single();
            var selector = (ListBox)settingsPage.FindName("CategorySelector");
            selector.SelectedItem = selector.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "transparency"));
            window.UpdateLayout();
            var transparency = (StorageTransparencyView)settingsPage.FindName("TransparencyView");
            Assert.True(transparency.IsVisible); Assert.Equal(4, transparency.Groups.Count);
            Descendants<Expander>(transparency).First().IsExpanded = true;
            window.UpdateLayout();
            PumpUntil(() => Descendants<TextBox>(transparency).Any(box => box.IsReadOnly && box.Text.EndsWith("settings.json", StringComparison.Ordinal)));
            Assert.Contains(Descendants<TextBox>(transparency), box => box.IsReadOnly && box.Text.EndsWith("settings.json", StringComparison.Ordinal));
            SaveGameDetailsVisual(window, "transparency");
            var settingsScroll = (ScrollViewer)settingsPage.FindName("SettingsScrollViewer");
            settingsScroll.ScrollToEnd(); window.UpdateLayout();
            Assert.True(settingsScroll.VerticalOffset > 0);
            var deactivate = model.DeactivateAsync(); PumpUntil(() => deactivate.IsCompleted); deactivate.GetAwaiter().GetResult();
            fixture.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.Notes = "Saved before closing";
            window.Close();
            Assert.True(window.IsVisible);
            fixture.SaveGate.SetResult();
            PumpUntil(() => !window.IsVisible);
            Assert.Equal("Saved before closing", fixture.Preferences[local.AppId].Notes);
        }
        finally { UiThemeService.Apply("Dark"); window.Close(); Application.Current.MainWindow = previous; }
    }
    private static void SaveGameDetailsVisual(MainWindow window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        PumpDispatcher(TimeSpan.FromMilliseconds(380)); window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, "game-details-" + name + ".png")); encoder.Save(stream);
    }
    private sealed class GameDetailsFixture : IGameDetailsDataService, IGamePreferencesStore, IPlayTimeService, IGameLaunchService
    {
        public Dictionary<int, GamePreferences> Preferences { get; } = new();
        public int Launches { get; private set; }
        public TaskCompletionSource? SaveGate { get; set; }
        public Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default) => Task.FromResult(new GameOverview("ARC Raiders is a multiplayer extraction adventure, set in a lethal future earth, ravaged by a mysterious mechanized threat known as ARC.", "Embark Studios", "Embark Studios")
        {
            DetailedDescription = "Game description (test fixture)\n\nExplore the surface, prepare your loadout and return to the extraction point.\n\nThis section verifies that the complete game information remains reachable below the hero and overview.",
            ReleaseDate = "30 Oct, 2025 (test fixture)", Genres = "Action · Adventure (test fixture)",
            Languages = "English, German (test fixture)", Features = "Online co-op · Steam achievements (test fixture)",
            SystemRequirements = "Minimum (test fixture)\nOS: Windows 10\nMemory: 16 GB RAM\n\nRecommended (test fixture)\nOS: Windows 11\nMemory: 32 GB RAM",
            Screenshots = FixtureScreenshots(),
            News = [new("Latest update (test fixture)", "Update cards are loaded automatically from the official Steam announcements feed. This is a layout fixture, not live news.", "8 Oct 2026", new Uri("https://store.steampowered.com/news/app/1808500"))]
        });
        private static IReadOnlyList<GameScreenshot> FixtureScreenshots()
        {
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            if (string.IsNullOrWhiteSpace(folder)) return [];
            return new[] { "1808500_header.jpg", "1808500_hero.jpg", "1808500_portrait.jpg" }
                .Select(file => Path.GetFullPath(Path.Combine(folder, file))).Where(File.Exists)
                .Select(file => new GameScreenshot(new Uri(file), new Uri(file))).ToArray();
        }
        public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default) => Task.FromResult(appId <= 0
            ? GameAchievementSnapshot.Unavailable("Steam achievements are not available for this local game.")
            : new GameAchievementSnapshot([new("fixture-first", "First extraction (test fixture)", "Complete an extraction.", true, "", DateTimeOffset.UtcNow.AddDays(-3)), new("fixture-second", "A new loadout (test fixture)", "Prepare a loadout.", true, "", DateTimeOffset.UtcNow.AddDays(-2)), new("fixture-third", "Return to the surface (test fixture)", "Complete another run.", false, "", null)], true, "Synced with Steam (test fixture)"));
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default) => Task.FromResult(Preferences.GetValueOrDefault(appId) ?? new());
        public async Task SaveAsync(int appId, GamePreferences value, CancellationToken token = default) { if (SaveGate is not null) await SaveGate.Task; Preferences[appId] = value; }
        Task<PlayTimeSnapshot> IPlayTimeService.ReadAsync(CancellationToken token) => Task.FromResult(new PlayTimeSnapshot(1, DateTimeOffset.UtcNow, [new(1808500, 7500, false), new(-42, 1020, false)]));
        public Task<DateTimeOffset?> ReadLastPlayedAsync(int appId, CancellationToken token = default) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(-1));
        public Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games) => Task.CompletedTask;
        public Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default) { Launches++; return Task.FromResult(new GameLaunchResult(true, "Launch requested.")); }
    }
}
