using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckBetterSteamTools(IServiceProvider provider, BetterSteamToolsFixture fixture)
    {
        var model = provider.GetRequiredService<BetterSteamToolsViewModel>();
        fixture.Installed = false;
        model.RefreshDetection();
        Assert.True(model.InstallBackendCommand.CanExecute(null));
        model.GameInput = "https://store.steampowered.com/app/480/Spacewar/";
        Assert.False(model.AddGameCommand.CanExecute(null));
        var install = model.InstallBackendCommand.ExecuteAsync(null);
        PumpUntil(() => install.IsCompleted); install.GetAwaiter().GetResult();
        Assert.True(model.BackendInstalled);
        Assert.Equal(1, fixture.InstallCalls);
        Assert.True(model.AddGameCommand.CanExecute(null));
        var add = model.AddGameCommand.ExecuteAsync(null);
        PumpUntil(() => add.IsCompleted); add.GetAwaiter().GetResult();
        Assert.Equal((480, (ManifestSource?)null), fixture.LastSource);
        Assert.Equal(480, Assert.Single(model.AddedGames).AppId);
        model.SelectedSource = model.Sources.Single(item => item.Source == ManifestSource.Hubcap);
        add = model.AddGameCommand.ExecuteAsync(null);
        PumpUntil(() => add.IsCompleted); add.GetAwaiter().GetResult();
        Assert.Equal((480, (ManifestSource?)ManifestSource.Hubcap), fixture.LastSource);
        model.GameInput = "";
        var import = model.ImportFilesAsync(["480.lua", "481_123.manifest"]);
        PumpUntil(() => import.IsCompleted); import.GetAwaiter().GetResult();
        Assert.Null(fixture.ImportAppId);
        Assert.Equal(2, fixture.ImportPaths!.Count);
        Assert.False(model.IsBusy);
        model.GameInput = "not an App ID";
        Assert.False(model.AddGameCommand.CanExecute(null));
        var previousCalls = fixture.ImportCalls;
        import = model.ImportFilesAsync(["480.lua"]);
        PumpUntil(() => import.IsCompleted); import.GetAwaiter().GetResult();
        Assert.Equal(previousCalls, fixture.ImportCalls);
        model.GameInput = "480";
        fixture.HoldOperation = true;
        add = model.AddGameCommand.ExecuteAsync(null);
        PumpUntil(() => model.IsBusy);
        Assert.False(model.BrowseFilesCommand.CanExecute(null));
        Assert.True(model.CancelCommand.CanExecute(null));
        model.CancelCommand.Execute(null);
        PumpUntil(() => add.IsCompleted); add.GetAwaiter().GetResult();
        Assert.False(model.IsBusy);
        Assert.Contains("cancelled", model.Status, StringComparison.OrdinalIgnoreCase);
        fixture.HoldOperation = false;
        model.GameInput = "";
        model.SelectedSource = model.Sources[0];
        model.RefreshDetection();
        var library = new LibraryPage();
        library.OpenDownloadSetup(new Steamy.Models.SteamCatalogItem { AppId = 4242, Name = "A game for Steam metadata" });
        Type? route = null;
        provider.GetRequiredService<INavigationService>().Attach(page => route = page);
        ((Button)library.FindName("AddToSteamToolsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpUntil(() => fixture.LastSource.Item1 == 4242 && !model.IsBusy);
        Assert.Equal(typeof(BetterSteamToolsPage), route);
        Assert.Equal((4242, (ManifestSource?)null), fixture.LastSource);
        model.GameInput = "";
        model.RefreshDetection();
    }

    private sealed class BetterSteamToolsFixture : IBetterSteamToolsService
    {
        public bool Installed { get; set; } = true;
        public bool HoldOperation { get; set; }
        public int InstallCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public (int, ManifestSource?) LastSource { get; private set; }
        public IReadOnlyList<string>? ImportPaths { get; private set; }
        public int? ImportAppId { get; private set; }
        public BetterSteamToolsState Detect(string? root = null) => new(@"C:\Program Files (x86)\Steam", true, Installed, [480], "Steam detected · sample backend and game configuration.");
        public Task<BetterSteamToolsResult> InstallBackendAsync(string root, IProgress<string>? progress = null, CancellationToken token = default)
        { InstallCalls++; Installed = true; return Task.FromResult(new BetterSteamToolsResult(true,"Backend ready.",[])); }
        public Task<BetterSteamToolsResult> ImportAsync(string root, IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default)
        { ImportCalls++; ImportPaths = paths; ImportAppId = appId; return Task.FromResult(new BetterSteamToolsResult(true,"Metadata added.",[480],1)); }
        public async Task<BetterSteamToolsResult> AddFromSourceAsync(string root, int appId, ManifestSource? source, IProgress<string>? progress = null, CancellationToken token = default)
        {
            LastSource = (appId,source);
            if (HoldOperation) await Task.Delay(Timeout.InfiniteTimeSpan,token);
            return new(true,"Game configuration added.",[appId]);
        }
    }
}

public sealed class BetterSteamToolsInputTests
{
    [Theory]
    [InlineData("480", 480)]
    [InlineData("https://store.steampowered.com/app/480/Spacewar/", 480)]
    [InlineData("0", null)]
    [InlineData("https://example.invalid/app/480", null)]
    [InlineData("https://store.steampowered.com.evil.invalid/app/480", null)]
    public void OnlyPositiveAppIdsAndOfficialStoreLinksAreAccepted(string input, int? expected)
        => Assert.Equal(expected, BetterSteamToolsViewModel.ParseAppId(input));
}
