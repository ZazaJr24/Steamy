using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// WPF-UI hosts page content in a dynamic scroll viewer unless the page opts out with
/// ScrollViewer.CanContentScroll="False". Without the opt-out no page can scroll, so these
/// source level checks keep the opt-out and the shared scroll host in place.
/// </summary>
public sealed class PageLayoutTests
{
    private static readonly string PagesDirectory = Path.GetDirectoryName(FindRepositoryFile("src/SteamContentManager/Pages/LibraryPage.xaml"))!;

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' above {AppContext.BaseDirectory}.");
    }

    public static TheoryData<string> PageFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(PagesDirectory, "*.xaml").OrderBy(path => path)) data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void EveryPageDisablesTheHostScrollViewer(string fileName)
    {
        var content = File.ReadAllText(Path.Combine(PagesDirectory, fileName));

        Assert.Contains("ScrollViewer.CanContentScroll=\"False\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedScrollHostIsDefinedWithWorkingSettings()
    {
        var styles = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/Resources/Styles.xaml"));

        Assert.Contains("x:Key=\"PageScrollHostStyle\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"PanningMode\" Value=\"VerticalOnly\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Focusable\" Value=\"False\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"VerticalScrollBarVisibility\" Value=\"Auto\" />", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationSurface_ContainsOnlyRequestedTopLevelAreas()
    {
        var navigation = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/MainWindow.xaml"));
        var toolsStart = navigation.IndexOf("Content=\"Tools\"", StringComparison.Ordinal);
        var toolsEnd = navigation.IndexOf("</ui:NavigationViewItem>", toolsStart, StringComparison.Ordinal);
        Assert.True(toolsStart >= 0 && toolsEnd > toolsStart);
        var toolsSection = navigation[toolsStart..toolsEnd];
        Assert.Contains("Content=\"Steamless\"", toolsSection, StringComparison.Ordinal);
        Assert.Contains("Content=\"Denuvo Activation\"", toolsSection, StringComparison.Ordinal);

        Assert.Contains("Content=\"Games\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Fixes\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Tools\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Settings\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Denuvo Fixes\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Steamless\"", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Denuvo Activation\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Goldberg\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"CreamInstaller", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Unsteam", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"ScreamAPI", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"XStoreUnlocker", navigation, StringComparison.Ordinal);
        Assert.Contains("Content=\"Dashboard\"", navigation, StringComparison.Ordinal);
        Assert.True(navigation.IndexOf("Content=\"Dashboard\"", StringComparison.Ordinal) < navigation.IndexOf("Content=\"Games\"", StringComparison.Ordinal));
        Assert.Contains("Content=\"Downloads\"", navigation, StringComparison.Ordinal);
        Assert.True(navigation.IndexOf("Content=\"Downloads\"", StringComparison.Ordinal) < navigation.IndexOf("Content=\"Settings\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Content=\"DepotDownloader\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Logs\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Manifests\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Depots\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Achievements\"", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolStatusPagesExposeAnInfoButton()
    {
        var page = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/Pages/ToolStatusPage.xaml"));
        var viewModel = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/ViewModels/ToolStatusViewModel.cs"));

        Assert.True(page.Contains("Icon=\"{ui:SymbolIcon Info24}\"", StringComparison.Ordinal) || page.Contains("Content=\"ⓘ\"", StringComparison.Ordinal), "The tool status page must expose an info button.");
        Assert.Contains("ToggleInfoCommand", page, StringComparison.Ordinal);
        Assert.Contains("ToggleInfoCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("InfoText", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPageReferencesRemovedDemoCommands()
    {
        string[] removed = { "AddDemoJobCommand", "GenerateMockCommand", "ExportJsonCommand", "AddGameCommand", "ImportCommand" };

        foreach (var file in Directory.EnumerateFiles(PagesDirectory, "*.xaml"))
        {
            var content = File.ReadAllText(file);
            foreach (var command in removed)
            {
                Assert.DoesNotContain(command, content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ProductionServices_DoNotUseDemoImplementations()
    {
        var registration = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/Services/ServiceRegistration.cs"));
        var app = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/App.xaml.cs"));

        Assert.Contains("AddSingleton<IAppDataStore, AppDataStore>()", registration, StringComparison.Ordinal);
        Assert.Contains("AddSteamContentManagerServices()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("IAppDataStore, DemoDataStore", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoDiskSpaceService", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoNotificationService", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoManifestService", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void NoViewModelSimulatesDownloadProgress()
    {
        var downloadManager = File.ReadAllText(FindRepositoryFile("src/SteamContentManager/Services/DownloadManager.cs"));

        Assert.DoesNotContain("RunDemoAsync", downloadManager, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo fallback", downloadManager, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViewModel_UsesWellKnownDnsEndpoints()
    {
        Assert.Equal("https://cloudflare-dns.com/dns-query", SettingsViewModel.DnsEndpointFor("Cloudflare DoH"));
        Assert.Equal("https://dns.google/dns-query", SettingsViewModel.DnsEndpointFor("Google DoH"));
        Assert.Equal("https://dns.quad9.net/dns-query", SettingsViewModel.DnsEndpointFor("Quad9 DoH"));
        Assert.Null(SettingsViewModel.DnsEndpointFor("Custom DoH"));
        Assert.Null(SettingsViewModel.DnsEndpointFor(null));
    }

    [Fact]
    public void SettingsViewModel_AppliesTheSelectedDnsMode()
    {
        var store = new AppDataStore();
        var logging = new InMemoryLoggingService(store, new NullLocalDatabase());
        var viewModel = new SettingsViewModel(
            store,
            new StubNavigationService(),
            logging,
            new SettingsViewModelTests.StubSettingsService(),
            new SettingsViewModelTests.StubCredentialService(),
            new DemoDnsResolver(),
            new SettingsViewModelTests.StubEndpointProbeService());

        viewModel.ApplyDnsMode("Google DoH");

        Assert.Equal("Google DoH", viewModel.Settings.DnsMode);
        Assert.Equal("https://dns.google/dns-query", viewModel.Settings.DnsEndpoint);
        Assert.Contains("Google DoH", viewModel.DnsStatus);
    }

    [Theory]
    [InlineData("app_730_depot_730.manifest", 730, 730)]
    [InlineData("app_1172470_depot_1172471.manifest", 1172470, 1172471)]
    [InlineData("APP_570_DEPOT_570.MANIFEST", 570, 570)]
    [InlineData("something-else.manifest", 0, 0)]
    [InlineData("", 0, 0)]
    public void ManifestFileName_IsParsedHonestlyOrReturnsZeros(string fileName, int appId, int depotId)
    {
        Assert.Equal((appId, depotId), ManifestViewModel.ParseManifestFileName(fileName));
    }

    private sealed class StubNavigationService : INavigationService
    {
        public void Attach(Action<Type> navigate) { }
        public void Detach() { }
        public void Navigate<TPage>() { }
    }

    private sealed class DemoDnsResolver : IDnsResolverService
    {
        public Task<DnsDiagnosticResult> DiagnoseAsync(string host, string mode, string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new DnsDiagnosticResult(true, host, mode, Array.Empty<string>(), 0, "ok"));
    }
}

public static class SettingsViewModelTests
{
    public sealed class StubSettingsService : ISettingsService
    {
        private AppSettings _settings = new();

        public int SaveCount { get; private set; }

        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { SaveCount++; _settings = settings; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken cancellationToken = default) { _settings = new AppSettings(); return Task.CompletedTask; }
    }

    public sealed class StubCredentialService : ISecureCredentialService
    {
        private readonly Dictionary<string, string> _values = new();

        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { SaveCount++; _values[key] = value; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { DeleteCount++; _values.Remove(key); return Task.CompletedTask; }
    }

    /// <summary>Answers a probe without a network call, but records what was probed.</summary>
    public sealed class StubEndpointProbeService : IEndpointProbeService
    {
        public List<string> Probed { get; } = new();
        public EndpointProbeResult Result { get; set; } = new(true, 200, 12, "example.com answered with HTTP 200 (OK).");

        public Task<EndpointProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
        {
            Probed.Add(url);
            return Task.FromResult(Result);
        }
    }
}
