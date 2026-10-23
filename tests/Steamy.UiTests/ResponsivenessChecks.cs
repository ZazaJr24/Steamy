using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Pages;
using Steamy.ViewModels;

namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckResponsiveness(IServiceProvider services)
    {
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { Width = 1440, Height = 900, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(200));
            window.RootNavigationView.Navigate(typeof(FriendsPage)); PumpUntil(() => Descendants<FriendsPage>(window).Any());
            var model = services.GetRequiredService<DiscordFriendsViewModel>(); model.SelectedFriend = model.Friends.First(friend => friend.IsFriend);
            for (var i = 0; i < 180; i++) model.Messages.Add(new((ulong)(98000 + i), 1, 1, 0, 8, "Long conversation " + i, DateTimeOffset.UtcNow.AddSeconds(i - 180), false, false, false));
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            var composer = (TextBox)Descendants<FriendsPage>(window).Single().FindName("MessageComposer");
            // Measure recurring chrome work separately from chat input and navigation.
            window.RefreshCaptionAppearance();
            var captionAllocated = GC.GetAllocatedBytesForCurrentThread();
            var captionStarted = Stopwatch.GetTimestamp();
            for (var i = 0; i < 200; i++) window.RefreshCaptionAppearance();
            var captionElapsedMs = Stopwatch.GetElapsedTime(captionStarted).TotalMilliseconds;
            captionAllocated = GC.GetAllocatedBytesForCurrentThread() - captionAllocated;
            var allocatedBeforeTyping = GC.GetTotalAllocatedBytes();
            var samples = new List<double>();
            var probes = Task.Run(async () =>
            {
                for (var i = 0; i < 120; i++)
                {
                    var began = Stopwatch.GetTimestamp();
                    var frame = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler? rendered = null;
                    await window.Dispatcher.InvokeAsync(() =>
                    {
                        rendered = (_, _) => { System.Windows.Media.CompositionTarget.Rendering -= rendered; frame.TrySetResult(Stopwatch.GetElapsedTime(began).TotalMilliseconds); };
                        System.Windows.Media.CompositionTarget.Rendering += rendered; composer.AppendText("x");
                    }, DispatcherPriority.Input);
                    try { samples.Add(await frame.Task.WaitAsync(TimeSpan.FromSeconds(2))); }
                    finally { await window.Dispatcher.InvokeAsync(() => System.Windows.Media.CompositionTarget.Rendering -= rendered); }
                    await Task.Delay(8);
                }
            });
            PumpUntil(() => probes.IsCompleted); probes.GetAwaiter().GetResult();
            Assert.Equal(120, composer.Text.Length);
            var allocatedDuringTyping = GC.GetTotalAllocatedBytes() - allocatedBeforeTyping;
            samples.Sort();
            var navigation = new List<double>();
            foreach (var route in new[] { typeof(LibraryPage), typeof(DownloadsPage), typeof(FriendsPage), typeof(ControllersPage), typeof(SettingsPage), typeof(FriendsPage) })
            {
                var began = Stopwatch.GetTimestamp(); window.RootNavigationView.Navigate(route);
                PumpUntil(() => Descendants<Page>(window).Any(page => page.GetType() == route)); window.UpdateLayout();
                navigation.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "responsiveness.json"), JsonSerializer.Serialize(new
                {
                    scenario = "1440x900 real WPF Friends view, 180 messages, 120 dispatcher input-to-next-composition-frame probes",
                    inputToRenderMedianMs = samples[60], inputToRenderP95Ms = samples[113], inputToRenderMaxMs = samples[^1],
                    navigationReadyMs = navigation,
                    chromeRefresh200ManagedBytes = captionAllocated, chromeRefresh200Ms = captionElapsedMs,
                    typing120ManagedBytes = allocatedDuringTyping,
                    processPrivateBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                    processWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                    limitations = "Synthetic dispatcher probes measured through the next WPF CompositionTarget.Rendering event, not physical keyboard-to-screen latency or network RTT. No live Discord account or GIF-search key used. Navigation includes layout; it is not a cold-start benchmark."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.True(samples[113] < 50, $"Input-to-render dispatcher p95 was {samples[113]:F1} ms.");
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
    }
}
