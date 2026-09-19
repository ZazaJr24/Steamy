using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The settings page used to report success while dropping what the user typed: the API key boxes
/// were never written anywhere and "Test connection" did nothing at all. These tests pin down the
/// behaviour that the page promises.
/// </summary>
public sealed class SettingsBehaviourTests
{
    private readonly AppDataStore _store = new();
    private readonly SettingsViewModelTests.StubSettingsService _settings = new();
    private readonly SettingsViewModelTests.StubCredentialService _credentials = new();
    private readonly SettingsViewModelTests.StubEndpointProbeService _probe = new();

    private SettingsViewModel CreateViewModel()
    {
        var logging = new InMemoryLoggingService(_store, new NullLocalDatabase());
        return new SettingsViewModel(_store, new StubNavigation(), logging, _settings, _credentials, new StubDns(), _probe);
    }

    [Fact]
    public async Task SavingStoresTheTypedApiKeyInsteadOfDroppingIt()
    {
        var viewModel = CreateViewModel();
        viewModel.SteamApiKeyInput = "ABCDEF0123456789";

        await viewModel.SaveAsync();

        Assert.Equal("ABCDEF0123456789", await _credentials.ReadAsync("steam-api-key"));
        Assert.Equal(string.Empty, viewModel.SteamApiKeyInput);
        Assert.Contains("Stored", viewModel.SteamCredentialStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavingWithEmptyBoxesKeepsTheStoredKey()
    {
        await _credentials.SaveAsync("steam-api-key", "already-there");
        var viewModel = CreateViewModel();

        await viewModel.SaveAsync();

        Assert.Equal("already-there", await _credentials.ReadAsync("steam-api-key"));
    }

    [Fact]
    public async Task ClearingCredentialsDeletesThemAndSaysSo()
    {
        await _credentials.SaveAsync("steam-api-key", "a");
        await _credentials.SaveAsync("ryuu-auth-key", "b");
        var viewModel = CreateViewModel();
        await viewModel.RefreshCredentialStatusAsync();
        Assert.Contains("Stored", viewModel.SteamCredentialStatus, StringComparison.OrdinalIgnoreCase);

        await viewModel.ClearCredentialsCommand.ExecuteAsync(null);

        Assert.Null(await _credentials.ReadAsync("steam-api-key"));
        Assert.Null(await _credentials.ReadAsync("ryuu-auth-key"));
        Assert.Contains("Not configured", viewModel.SteamCredentialStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("removed", viewModel.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheApiKeyIsNeverWrittenIntoTheSettingsFile()
    {
        var viewModel = CreateViewModel();
        viewModel.SteamApiKeyInput = "super-secret-key";

        await viewModel.SaveAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(viewModel.Settings);
        Assert.DoesNotContain("super-secret-key", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("99", 16)]
    [InlineData("0", 1)]
    [InlineData("4", 4)]
    public async Task QueueNumbersAreClampedIntoTheirRange(string typed, int expected)
    {
        var viewModel = CreateViewModel();

        viewModel.ParallelDownloadsText = typed;
        await viewModel.SaveAsync();

        Assert.Equal(expected, viewModel.Settings.ParallelDownloads);
        Assert.Contains(expected.ToString(), viewModel.ParallelDownloadsHint, StringComparison.Ordinal);
    }

    [Fact]
    public void GarbageInANumberFieldIsExplainedAndChangesNothing()
    {
        var viewModel = CreateViewModel();
        var before = viewModel.Settings.TimeoutSeconds;

        viewModel.TimeoutSecondsText = "sixty";

        Assert.Equal(before, viewModel.Settings.TimeoutSeconds);
        Assert.Contains("not a number", viewModel.TimeoutSecondsHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangesAreSavedAutomaticallyWithoutPressingSave()
    {
        var viewModel = CreateViewModel();
        var savesBefore = _settings.SaveCount;

        // Nothing on the page has to be pressed for this.
        viewModel.Settings.Notifications = !viewModel.Settings.Notifications;

        var saved = await WaitUntilAsync(() => _settings.SaveCount > savesBefore, TimeSpan.FromSeconds(6));

        Assert.True(saved, "The change was never written to disk.");
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("Saved", viewModel.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeavingThePageFlushesPendingChanges()
    {
        var viewModel = CreateViewModel();
        var savesBefore = _settings.SaveCount;

        viewModel.Settings.DebugLogging = !viewModel.Settings.DebugLogging;
        await viewModel.OnNavigatedFromAsync();

        Assert.True(_settings.SaveCount > savesBefore, "Leaving the page did not save the pending change.");
    }

    [Fact]
    public async Task TheEndpointChecksReallyProbeTheConfiguredAddress()
    {
        var viewModel = CreateViewModel();
        viewModel.Settings.SteamApiUrl = "https://api.example.test/base";
        viewModel.Settings.RyuuBaseUrl = "https://generator.example.test/";

        await viewModel.TestSteamCommand.ExecuteAsync(null);
        await viewModel.TestRyuuCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "https://api.example.test/base", "https://generator.example.test/" }, _probe.Probed);
        Assert.Contains("example.com", viewModel.SteamTestStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No auth key is stored", viewModel.RyuuTestStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheEndpointCheckReportsAFailureInsteadOfClaimingSuccess()
    {
        _probe.Result = new EndpointProbeResult(false, null, 3000, "api.example.test could not be reached (timeout).");
        var viewModel = CreateViewModel();

        await viewModel.TestSteamCommand.ExecuteAsync(null);

        Assert.Contains("could not be reached", viewModel.SteamTestStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResettingRestoresDefaultsAndTheNumberBoxes()
    {
        var viewModel = CreateViewModel();
        viewModel.Settings.ParallelDownloads = 12;
        viewModel.Settings.Notifications = false;

        await viewModel.ResetCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Settings.ParallelDownloads);
        Assert.True(viewModel.Settings.Notifications);
        Assert.Equal("2", viewModel.ParallelDownloadsText);
        Assert.Contains("reset", viewModel.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamIdAndDohEndpointAreValidatedInPlace()
    {
        var viewModel = CreateViewModel();

        viewModel.Settings.SteamId64 = "123";
        Assert.Contains("17 digits", viewModel.SteamIdHint, StringComparison.OrdinalIgnoreCase);

        viewModel.Settings.SteamId64 = "76561198000000000";
        Assert.Contains("Looks like", viewModel.SteamIdHint, StringComparison.OrdinalIgnoreCase);

        viewModel.Settings.DnsEndpoint = "http://insecure.example";
        Assert.Contains("https", viewModel.DnsEndpointHint, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    private sealed class StubNavigation : INavigationService
    {
        public void Attach(Action<Type> navigate) { }
        public void Detach() { }
        public void Navigate<TPage>() { }
    }

    private sealed class StubDns : IDnsResolverService
    {
        public Task<DnsDiagnosticResult> DiagnoseAsync(string host, string mode, string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new DnsDiagnosticResult(true, host, mode, Array.Empty<string>(), 7, "ok"));
    }
}
