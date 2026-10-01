using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Steamy.Controls;
using Steamy.Services;
using Steamy.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckScrollReversal()
    {
        var viewer = new ScrollViewer { Width = 300, Height = 160, CanContentScroll = false,
            Content = new Border { Height = 2400 } };
        SmoothScroll.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, Width = 340, Height = 210, ShowInTaskbar = false };
        window.Show();
        try
        {
            viewer.ScrollToVerticalOffset(300);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            static void Wheel(ScrollViewer target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Wheel(viewer, -120); Wheel(viewer, -120); Wheel(viewer, -120);
            var reversal = viewer.VerticalOffset;
            Wheel(viewer, 120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(viewer.VerticalOffset < reversal, "Reversing the wheel must reverse movement immediately, even during an unfinished animation.");
            viewer.ScrollToBottom();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Wheel(viewer, -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.InRange(viewer.VerticalOffset, 0, viewer.ScrollableHeight);
        }
        finally { window.Close(); SmoothScroll.SetEnabled(viewer, false); }
    }

    private static void CheckDlcSelection(IServiceProvider provider)
    {
        var fixture = new DlcSelectionFixture();
        var model = new CreamApiViewModel(fixture, provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IGameLocatorService>(), provider.GetRequiredService<ILoggingService>());
        PumpUntil(() => !model.IsLoadingGames);
        model.SelectedGame = new InstalledGameEntry(100, "First game", @"C:\Games\First");
        model.SelectedGame = new InstalledGameEntry(200, "Second game", @"C:\Games\Second");
        fixture.Requests[100].SetResult(new[] { new DlcEntry(101, "Stale DLC") }); // Some providers ignore cancellation.
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert.True(model.IsFetching);
        Assert.Empty(model.DlcList);
        fixture.Requests[200].SetResult(new[] { new DlcEntry(201, "Current DLC") });
        PumpUntil(() => !model.IsFetching);
        Assert.Equal(201, Assert.Single(model.DlcList).AppId);
        model.DeselectAllCommand.Execute(null);
        Assert.False(Assert.Single(model.DlcList).IsSelected);
        model.SelectAllCommand.Execute(null);
        Assert.True(Assert.Single(model.DlcList).IsSelected);
    }

    private sealed class DlcSelectionFixture : ICreamApiService
    {
        public Dictionary<int, TaskCompletionSource<IReadOnlyList<DlcEntry>>> Requests { get; } = new();
        public bool HasCachedDlls(DlcUnlockerMode mode) => true;
        public string ProxyAddress { get; set; } = "";
        public Task EnsureDllsAvailableAsync(DlcUnlockerMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtractDllsFromArchiveAsync(string archivePath, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<DlcEntry>> FetchDlcListAsync(int appId, CancellationToken ct = default)
        {
            var result = new TaskCompletionSource<IReadOnlyList<DlcEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(appId, result);
            return result.Task;
        }
        public CreamApiApplyResult ApplyToGameFolder(string gameFolder, int appId, IReadOnlyList<DlcEntry> dlcs,
            DlcUnlockerMode mode, string language, bool unlockAll, bool extraProtection, bool forceOffline) => throw new InvalidOperationException("Fixture never installs DLLs.");
        public CreamApiApplyResult RestoreOriginalDlls(string gameFolder) => throw new InvalidOperationException("Fixture never changes DLLs.");
    }
}
