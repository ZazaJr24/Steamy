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
        var detectedSteam = SteamLibraryService.FindSteamRoot();
        var setup = new FirstRunSetup(settings);
        setup.Measure(new Size(800, 700));
        setup.Arrange(new Rect(0, 0, 800, 700));
        setup.UpdateLayout();

        Assert.Equal(detectedSteam ?? string.Empty, Assert.IsType<TextBox>(setup.FindName("SteamFolderText")).Text);
        Assert.Equal("System Default", Assert.IsType<ComboBox>(setup.FindName("LanguageSelector")).SelectedItem);
        Assert.Equal("Dark", Assert.IsType<ComboBox>(setup.FindName("AppearanceSelector")).SelectedItem);
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("PreferencesStep")).Visibility);

        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("FoldersStep")).Visibility);
        ClickButton(setup, "NextButton");
        Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(setup.FindName("DownloadsStep")).Visibility);

        ((ComboBox)setup.FindName("LanguageSelector")!).SelectedItem = "Deutsch";
        ((ComboBox)setup.FindName("AppearanceSelector")!).SelectedItem = "Light";
        ((TextBox)setup.FindName("ParallelJobsText")!).Text = "4";
        ((TextBox)setup.FindName("ConnectionsText")!).Text = "32";
        ((TextBox)setup.FindName("SpeedLimitText")!).Text = "120";
        ((TextBox)setup.FindName("RetryCountText")!).Text = "5";

        ClickButton(setup, "NextButton");
        PumpUntil(() => settings.SaveCount == 1);
        Assert.Equal("Deutsch", settings.Load().Language);
        Assert.Equal("Light", settings.Load().Appearance);
        Assert.Equal(4, settings.Load().ParallelDownloads);
        Assert.Equal(32, settings.Load().DownloadConnections);
        Assert.Equal(120, settings.Load().DownloadRateLimitMiB);
        Assert.Equal(5, settings.Load().RetryCount);
        Assert.True(settings.Load().VerifyAfterDownload);
        Assert.True(settings.Load().AutoResume);

        var existingSettings = new FirstRunMemorySettings { Language = "Deutsch", DownloadFolder = "existing-folder" };
        var skippedSetup = new FirstRunSetup(existingSettings);
        ClickButton(skippedSetup, "SkipButton");
        PumpUntil(() => existingSettings.SaveCount == 1);
        Assert.Equal("Deutsch", existingSettings.Load().Language);
        Assert.Equal("existing-folder", existingSettings.Load().DownloadFolder);
        UiThemeService.Apply("Dark");
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
        public string Language { set => _settings.Language = value; }
        public string DownloadFolder { set => _settings.DownloadFolder = value; }
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            return Task.CompletedTask;
        }
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
