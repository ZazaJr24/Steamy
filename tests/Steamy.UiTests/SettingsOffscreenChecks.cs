using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Pages;
using Steamy.Controls;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckSettingsOffscreen(IServiceProvider services)
    {
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(folder);
        var samples = new List<object>();
        UiThemeService.Apply("Dark");
        _ = new SettingsPage(); // Shared style/model initialization is outside repeat-construction measurements.
        for (var index = 0; index < 5; index++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var began = Stopwatch.GetTimestamp();
            var page = new SettingsPage();
            var elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Add(new { index, elapsedMs, allocatedBytes = bytes });
        }
        foreach (var theme in new[] { "Dark", "Light" })
        {
            UiThemeService.Apply(theme);
            var page = new SettingsPage();
            var selector = (ListBox)page.FindName("CategorySelector");
            foreach (var category in new[] { 0, 1, 2, 3, 4, 5 })
            {
                selector.SelectedIndex = category;
                page.Measure(new Size(1440, 800)); page.Arrange(new Rect(0, 0, 1440, 800)); page.UpdateLayout();
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                var image = new RenderTargetBitmap(1440, 800, 96, 96, PixelFormats.Pbgra32); image.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(folder, $"settings-{theme.ToLowerInvariant()}-{category}.png")); encoder.Save(output);
            }
        }
        if (Environment.GetEnvironmentVariable("STEAMY_UI_SETTINGS_DEFER_EXPECT") == "1") CheckDeferredSettings(services);
        File.WriteAllText(Path.Combine(folder, "construction.json"), JsonSerializer.Serialize(new
        {
            scope = "Five warm repeat constructors on one STA dispatcher, shared styles/model initialized. Plain test Application resources; no Window, app startup or production navigation timing.",
            applicationWindows = Application.Current.Windows.Count, samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    }

    private static void CheckDeferredSettings(IServiceProvider services)
    {
        var page = new SettingsPage();
        var model = services.GetRequiredService<SettingsViewModel>();
        var sections = ((StackPanel)page.FindName("SectionsPanel")).Children.OfType<DeferredSettingsSection>().ToArray();
        Assert.Equal(13, sections.Length);
        Assert.All(sections.Where(section => !Equals(section.Tag, "general")), section => Assert.False(section.IsRealized));
        Assert.IsType<BackgroundOptions>(page.FindName("BackgroundPreferences"));
        var search = (TextBox)page.FindName("SettingsSearch");
        var selector = (ListBox)page.FindName("CategorySelector");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            page.Measure(new Size(1440, 800)); page.Arrange(new Rect(0, 0, 1440, 800)); page.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            var toggle = (Wpf.Ui.Controls.ToggleSwitch)page.FindName("DashboardSearchToggle");
            var previous = model.Settings.DashboardSearch;
            toggle.IsChecked = !previous;
            Assert.Equal(!previous, model.Settings.DashboardSearch);
            toggle.IsChecked = previous;
            search.Text = "resume"; PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert.Contains(sections, section => section.Visibility == Visibility.Visible && Equals(section.Tag, "downloads"));
            Assert.All(sections.Where(section => Equals(section.Tag, "services")), section => Assert.False(section.IsRealized));
            search.Clear(); selector.SelectedIndex = 2; PumpDispatcher(TimeSpan.FromMilliseconds(250));
            foreach (var name in new[] { "SteamApiKeyBox", "RyuuAuthKeyBox", "HubcapApiKeyBox", "DepotBoxApiKeyBox", "ManifestHubApiKeyBox", "MirrorTokenBox" })
                Assert.IsType<PasswordBox>(page.FindName(name));
            var input = (PasswordBox)page.FindName("SteamApiKeyBox");
            input.Password = "offline-settings-test-marker";
            Assert.Equal(input.Password, model.SteamApiKeyInput);
            var save = model.SaveCredentialsAsync(); PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult();
            Assert.Empty(input.Password);
            Assert.All(sections, section => Assert.DoesNotContain("offline-settings-test-marker", section.SearchLabels));
            Assert.False(sections.Single(section => section.IsDiscordSection).IsRealized);
            Assert.Null(page.FindName("SettingsDiscordConnection"));
            search.Text = "no-setting-matches-987654"; PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert.DoesNotContain(sections, section => section.Visibility == Visibility.Visible);
        }
        finally { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        var restored = new SettingsPage();
        Assert.Equal(search.Text, ((TextBox)restored.FindName("SettingsSearch")).Text);
        Assert.Equal(selector.SelectedIndex, ((ListBox)restored.FindName("CategorySelector")).SelectedIndex);
        // Leave the shared fixture ready for a different test, without saving secrets.
        model.NavigationSearchText = string.Empty; model.NavigationCategoryIndex = 0;
    }

    private sealed class SettingsCredentialsFixture : ISecureCredentialService
    {
        private readonly Dictionary<string, string> _values = new();
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { _values.Remove(key); return Task.CompletedTask; }
    }
}
