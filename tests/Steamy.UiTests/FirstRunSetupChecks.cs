using System.Windows;
using System.Windows.Controls;
using Steamy.Models;
using Steamy.Services;
using Steamy.Views;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckFirstRunSetup()
    {
        var settings = new FirstRunMemorySettings();
        var scanner = new ReadyScannerFixture();
        var setup = new FirstRunSetup(settings, downloaderSetup: new ReadyDownloaderFixture(), gameScanner: scanner);
        setup.Measure(new Size(800, 700));
        setup.Arrange(new Rect(0, 0, 800, 700));
        setup.UpdateLayout();

        Assert.Equal("en-US · English", Assert.IsType<ComboBox>(setup.FindName("LanguageSelector")).SelectedItem);
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("GameScanStep")).Visibility);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(setup.FindName("LanguageStep")).Visibility);
        Assert.Equal(0, scanner.Requests); // Known-library discovery does not force a whole-PC scan.
        ClickButton(setup, "ScanGamesButton");
        Assert.Equal(1, scanner.Requests);
        Assert.Contains("1 games found", Assert.IsType<TextBlock>(setup.FindName("GameScanStatus")).Text);
        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("LanguageStep")).Visibility);
        var languageSelector = Assert.IsType<ComboBox>(setup.FindName("LanguageSelector"));
        var germanLanguage = Assert.IsType<string>(languageSelector.Items.Cast<string>().First(item => item.StartsWith("de-DE", StringComparison.Ordinal)));
        languageSelector.SelectedItem = germanLanguage;
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("AppearanceStep")).Visibility);
        ((RadioButton)setup.FindName("LightAppearance")).IsChecked = true;
        var trailers = (Wpf.Ui.Controls.ToggleSwitch)setup.FindName("TrailerPreviewsToggle");
        Assert.True(trailers.IsChecked); trailers.IsChecked = false;
        var backgrounds = (Steamy.Controls.BackgroundOptions)setup.FindName("SetupBackgroundPreferences");
        ((Wpf.Ui.Controls.ToggleSwitch)backgrounds.FindName("AmbientToggle")).IsChecked = false;
        ((TextBox)backgrounds.FindName("ColorHex")).Text = "#314A72";
        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("FoldersStep")).Visibility);
        var advanced = (Expander)setup.FindName("AdvancedPreferences"); advanced.IsExpanded = true;
        ((TextBox)setup.FindName("ParallelJobsText")!).Text = "4";
        ((TextBox)setup.FindName("ConnectionsText")!).Text = "32";
        ((TextBox)setup.FindName("SpeedLimitText")!).Text = "120";
        ((TextBox)setup.FindName("RetryCountText")!).Text = "5";
        ClickButton(setup, "NextButton");
        PumpUntil(() => Assert.IsType<StackPanel>(setup.FindName("ReadyStep")).Visibility == Visibility.Visible);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal("Save & finish", Assert.IsAssignableFrom<Button>(setup.FindName("NextButton")).Content);
        ClickButton(setup, "NextButton");
        PumpUntil(() => settings.SaveCount == 1);
        Assert.Equal(germanLanguage, settings.Load().Language);
        Assert.Equal("Light", settings.Load().Appearance);
        Assert.False(settings.Load().AutoplayTrailers);
        Assert.False(settings.Load().AmbientEnabled);
        Assert.Equal("#314A72", settings.Load().BackgroundColor);
        Assert.False(UiThemeService.AmbientEnabled);
        Assert.False(Steamy.Controls.MotionPreferences.TrailerPreviewsEnabled);
        Assert.Equal(4, settings.Load().ParallelDownloads);
        Assert.Equal(32, settings.Load().DownloadConnections);
        Assert.Equal(120, settings.Load().DownloadRateLimitMiB);
        Assert.Equal(5, settings.Load().RetryCount);
        Assert.True(settings.Load().VerifyAfterDownload);
        Assert.True(settings.Load().AutoResume);
        // This smoke fixture is detached from a Window, so explicitly release its WPF
        // animation clocks instead of leaving them attached to the shared test dispatcher.
        var stepContent = Assert.IsAssignableFrom<UIElement>(setup.FindName("StepContent"));
        stepContent.BeginAnimation(UIElement.OpacityProperty, null);
        var scale = (System.Windows.Media.ScaleTransform)setup.FindName("StepScale")!;
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        var slide = (System.Windows.Media.TranslateTransform)setup.FindName("StepSlide")!;
        slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        App.ApplyCulture("System Default");
        UiThemeService.Apply("Dark");
        UiThemeService.ConfigureBackground(new AppSettings());
        Steamy.Controls.MotionPreferences.ConfigureTrailers(true);
    }

    private static void ClickButton(FirstRunSetup setup, string name)
    {
        var button = Assert.IsAssignableFrom<Button>(setup.FindName(name));
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private sealed class FirstRunMemorySettings : ISettingsService
    {
        private readonly AppSettings _settings = new() { AutoUpdate = false };
        public int SaveCount { get; private set; }
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            return Task.CompletedTask;
        }
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReadyDownloaderFixture : IRyuuGameDownloadService
    {
        public IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent) => [];
        public Task<bool> EnsureDepotDownloaderModAsync(IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder, ManifestSource source,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RyuuGameDownloadResult> ResumeDownloadAsync(int appId, string targetFolder,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ReadyScannerFixture : IPcGameScanService
    {
        public int Requests;
        public Task<GameScanResult> ScanAsync(IProgress<GameScanProgress>? progress = null, CancellationToken token = default, IReadOnlyList<string>? roots = null)
        {
            Requests++;
            return Task.FromResult(new GameScanResult([new(42, "Fixture game", @"C:\Games\Fixture")], 23, 1, false));
        }
    }
}
