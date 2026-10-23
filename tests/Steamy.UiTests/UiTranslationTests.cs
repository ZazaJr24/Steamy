using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class UiTranslationTests
{
    [Fact]
    public void EnglishFastPathPreservesLaterTranslationAndEnglishRestorationWithoutChangingBoundText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var directory = Path.Combine(Path.GetTempPath(), "Steamy-translation-" + Guid.NewGuid().ToString("N"));
            var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
            var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
            var currentCulture = CultureInfo.CurrentCulture; var currentUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                Directory.CreateDirectory(directory);
                var cache = Path.Combine(directory, "translations.json");
                File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, string> { ["de\0Home"] = "Startseite" }));
                var translation = new UiTranslationService(new Settings(), cache);
                var label = new TextBlock { Text = "Home" }; var dynamicLabel = new TextBlock();
                BindingOperations.SetBinding(dynamicLabel, TextBlock.TextProperty, new Binding("Text") { Source = new { Text = "Live status" } });
                var root = new StackPanel(); root.Children.Add(label); root.Children.Add(dynamicLabel);
                Complete(translation.ApplyToAsync(root, "English")); Assert.Equal("Home", label.Text);
                Complete(translation.ApplyToAsync(root, "Deutsch")); Assert.Equal("Startseite", label.Text);
                Complete(translation.ApplyToAsync(root, "English")); Assert.Equal("Home", label.Text);
                Complete(translation.ApplyToAsync(root, "Deutsch")); Assert.Equal("Startseite", label.Text);
                // Originals are shared, so a second service must also restore the tree.
                Complete(new UiTranslationService(new Settings(), cache).ApplyToAsync(root, "English"));
                Assert.Equal("Home", label.Text); Assert.Equal("Live status", dynamicLabel.Text);
                Assert.NotNull(BindingOperations.GetBindingExpression(dynamicLabel, TextBlock.TextProperty));
            }
            catch (Exception error) { failure = error; }
            finally
            {
                CultureInfo.DefaultThreadCurrentCulture = defaultCulture; CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
                CultureInfo.CurrentCulture = currentCulture; CultureInfo.CurrentUICulture = currentUiCulture;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Complete(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(task.IsCompleted, "The seeded translation cache should complete without a network request.");
        task.GetAwaiter().GetResult();
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new() { Language = "English" };
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
