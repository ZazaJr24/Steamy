using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>
/// The settings page. Everything on it writes into one <see cref="AppSettings"/> instance which is
/// saved automatically shortly after the last change, so a toggle is never lost because the Save
/// button was missed. Credentials are handled separately: they are written through the encrypted
/// credential store and never end up in the settings file or in an export.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>How long the page waits for further changes before it writes to disk.</summary>
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Typing a secret is a little more expensive to store, so it waits slightly longer.</summary>
    private static readonly TimeSpan CredentialAutosaveDelay = TimeSpan.FromMilliseconds(900);

    private const string SteamApiKeyName = "steam-api-key";
    private const string RyuuAuthKeyName = "ryuu-auth-key";
    private const string HubcapApiKeyName = "hubcap-api-key";
    private const string DepotBoxApiKeyName = "depotbox-api-key";
    private const string MirrorTokenName = FixSource.TokenCredentialName;

    private readonly ISettingsService _settingsService;
    private readonly ISecureCredentialService _credentials;
    private readonly IDnsResolverService _dns;
    private readonly IEndpointProbeService _probe;

    private CancellationTokenSource? _autosaveCts;
    private CancellationTokenSource? _credentialAutosaveCts;
    private System.ComponentModel.PropertyChangedEventHandler? _settingsChangedHandler;
    private string _status = "Changes are saved automatically.";
    private string _steamCredentialStatus = "Not configured";
    private string _ryuuCredentialStatus = "Not configured";
    private string _hubcapCredentialStatus = "Not configured";
    private string _depotBoxCredentialStatus = "Not configured";
    private string _mirrorCredentialStatus = "Not configured";
    private string _mirrorTestStatus = "Not checked yet.";
    private string _steamTestStatus = "Not checked yet.";
    private string _ryuuTestStatus = "Not checked yet.";
    private string _hubcapTestStatus = "Not checked yet.";
    private string _hubcapUsageInfo = string.Empty;
    private double _hubcapUsagePercent;
    private string _depotBoxTestStatus = "Not checked yet.";
    private string _depotBoxUsageInfo = string.Empty;
    private string _dnsStatus = "App-only DNS diagnostics are ready.";
    private string _dnsAddresses = "—";
    private string _dnsLatency = "—";
    private string _parallelText = string.Empty;
    private string _retryText = string.Empty;
    private string _timeoutText = string.Empty;
    private string _parallelHint = string.Empty;
    private string _retryHint = string.Empty;
    private string _timeoutHint = string.Empty;
    private string _connectionsText = string.Empty;
    private string _connectionsHint = string.Empty;
    private bool _isBusy;
    private bool _hasUnsavedChanges;

    public SettingsViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ISettingsService settingsService,
        ISecureCredentialService credentials,
        IDnsResolverService dns,
        IEndpointProbeService probe) : base(store, navigation, logging)
    {
        _settingsService = settingsService;
        _credentials = credentials;
        _dns = dns;
        _probe = probe;

        Settings = _settingsService.Load();
        AttachSettings(Settings);
        SyncNumberFields();

        SaveCommand = new AsyncRelayCommand(() => SaveAsync("Saved by hand."));
        ClearCredentialsCommand = new AsyncRelayCommand(ClearCredentialsAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        TestSteamCommand = new AsyncRelayCommand(TestSteamAsync);
        TestRyuuCommand = new AsyncRelayCommand(TestRyuuAsync);
        TestHubcapCommand = new AsyncRelayCommand(TestHubcapAsync);
        TestDepotBoxCommand = new AsyncRelayCommand(TestDepotBoxAsync);
        TestDnsCommand = new AsyncRelayCommand(TestDnsAsync);
        TestMirrorCommand = new AsyncRelayCommand(TestMirrorAsync);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);

        _ = RefreshCredentialStatusAsync();
    }

    /// <summary>Raised after credentials were stored or deleted so the page can clear its boxes.</summary>
    public event EventHandler? CredentialInputsCleared;

    /// <summary>
    /// Called by the page whenever a password box changes. Secrets are deliberately not part of the
    /// <see cref="AppSettings"/> object, so the settings autosave never sees them — without this a
    /// typed key was only ever written when the user pressed Save by hand, and "Test connection"
    /// kept reporting "no key stored".
    /// </summary>
    public void OnCredentialInputChanged(string what)
    {
        _credentialAutosaveCts?.Cancel();
        _credentialAutosaveCts?.Dispose();
        _credentialAutosaveCts = new CancellationTokenSource();
        SaveStatus = $"{what} typed — storing it…";
        _ = StoreCredentialsAfterDelayAsync(_credentialAutosaveCts.Token);
    }

    private async Task StoreCredentialsAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(CredentialAutosaveDelay, token);
            if (token.IsCancellationRequested) return;

            await SaveCredentialsAsync();
            SaveStatus = $"Credential stored · {DateTime.Now:HH:mm:ss}";
        }
        catch (TaskCanceledException)
        {
            // A newer keystroke is on its way; that one stores the final value.
        }
    }

    public AppSettings Settings { get; private set; }

    public string[] Languages { get; } = { "System Default", "Deutsch", "English" };
    public string[] Appearances { get; } = { "System", "Light", "Dark" };
    public string[] BackdropStyles { get; } = { "None", "Mica", "Acrylic", "Tabbed" };
    public string[] DnsModes { get; } = { "System resolver", "Cloudflare DoH", "Google DoH", "Quad9 DoH", "Custom DoH" };

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string SteamApiKeyInput { get; set; } = string.Empty;

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string RyuuAuthKeyInput { get; set; } = string.Empty;

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string HubcapApiKeyInput { get; set; } = string.Empty;

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string DepotBoxApiKeyInput { get; set; } = string.Empty;

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string MirrorTokenInput { get; set; } = string.Empty;

    public string SteamCredentialStatus
    {
        get => _steamCredentialStatus;
        private set => SetProperty(ref _steamCredentialStatus, value);
    }

    public string RyuuCredentialStatus
    {
        get => _ryuuCredentialStatus;
        private set => SetProperty(ref _ryuuCredentialStatus, value);
    }

    public string HubcapCredentialStatus
    {
        get => _hubcapCredentialStatus;
        private set => SetProperty(ref _hubcapCredentialStatus, value);
    }

    public string DepotBoxCredentialStatus
    {
        get => _depotBoxCredentialStatus;
        private set => SetProperty(ref _depotBoxCredentialStatus, value);
    }

    public string MirrorCredentialStatus
    {
        get => _mirrorCredentialStatus;
        private set => SetProperty(ref _mirrorCredentialStatus, value);
    }

    public string MirrorTestStatus
    {
        get => _mirrorTestStatus;
        private set => SetProperty(ref _mirrorTestStatus, value);
    }

    public string HubcapTestStatus
    {
        get => _hubcapTestStatus;
        private set => SetProperty(ref _hubcapTestStatus, value);
    }

    public string HubcapUsageInfo
    {
        get => _hubcapUsageInfo;
        private set => SetProperty(ref _hubcapUsageInfo, value);
    }

    public double HubcapUsagePercent
    {
        get => _hubcapUsagePercent;
        private set => SetProperty(ref _hubcapUsagePercent, value);
    }

    public string DepotBoxTestStatus
    {
        get => _depotBoxTestStatus;
        private set => SetProperty(ref _depotBoxTestStatus, value);
    }

    public string DepotBoxUsageInfo
    {
        get => _depotBoxUsageInfo;
        private set => SetProperty(ref _depotBoxUsageInfo, value);
    }

    public string SteamTestStatus
    {
        get => _steamTestStatus;
        private set => SetProperty(ref _steamTestStatus, value);
    }

    public string RyuuTestStatus
    {
        get => _ryuuTestStatus;
        private set => SetProperty(ref _ryuuTestStatus, value);
    }

    public string DnsStatus
    {
        get => _dnsStatus;
        private set => SetProperty(ref _dnsStatus, value);
    }

    public string DnsAddresses
    {
        get => _dnsAddresses;
        private set => SetProperty(ref _dnsAddresses, value);
    }

    public string DnsLatency
    {
        get => _dnsLatency;
        private set => SetProperty(ref _dnsLatency, value);
    }

    public string SaveStatus
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    // ---- number fields: text in, checked value out ------------------------------------------

    public string ParallelDownloadsText
    {
        get => _parallelText;
        set
        {
            if (!SetProperty(ref _parallelText, value)) return;
            ApplyNumber(value, 1, 16, parsed => Settings.ParallelDownloads = parsed, hint => ParallelDownloadsHint = hint, "parallel jobs");
        }
    }

    public string ParallelDownloadsHint
    {
        get => _parallelHint;
        private set => SetProperty(ref _parallelHint, value);
    }

    public string RetryCountText
    {
        get => _retryText;
        set
        {
            if (!SetProperty(ref _retryText, value)) return;
            ApplyNumber(value, 0, 10, parsed => Settings.RetryCount = parsed, hint => RetryCountHint = hint, "retries");
        }
    }

    public string RetryCountHint
    {
        get => _retryHint;
        private set => SetProperty(ref _retryHint, value);
    }

    public string TimeoutSecondsText
    {
        get => _timeoutText;
        set
        {
            if (!SetProperty(ref _timeoutText, value)) return;
            ApplyNumber(value, 5, 3600, parsed => Settings.TimeoutSeconds = parsed, hint => TimeoutSecondsHint = hint, "seconds");
        }
    }

    public string DownloadConnectionsText
    {
        get => _connectionsText;
        set
        {
            if (!SetProperty(ref _connectionsText, value)) return;
            ApplyNumber(value, 1, DepotDownloaderArgumentBuilder.MaxDownloadsLimit, parsed => Settings.DownloadConnections = parsed, hint => DownloadConnectionsHint = hint, "connections");
        }
    }

    public string DownloadConnectionsHint
    {
        get => _connectionsHint;
        private set => SetProperty(ref _connectionsHint, value);
    }

    public string TimeoutSecondsHint
    {
        get => _timeoutHint;
        private set => SetProperty(ref _timeoutHint, value);
    }

    /// <summary>Live hint for the SteamID64 field: it is either empty, or 17 digits.</summary>
    public string SteamIdHint
    {
        get
        {
            var value = Settings.SteamId64?.Trim() ?? string.Empty;
            if (value.Length == 0) return "Optional — a SteamID64 is 17 digits.";
            return Regex.IsMatch(value, @"^\d{17}$")
                ? "Looks like a SteamID64."
                : $"A SteamID64 has 17 digits; this has {value.Length}.";
        }
    }

    /// <summary>Live hint for the DoH endpoint: the custom mode needs an https address.</summary>
    public string DnsEndpointHint
    {
        get
        {
            var value = Settings.DnsEndpoint?.Trim() ?? string.Empty;
            if (value.Length == 0) return "Required for Custom DoH only.";
            return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "Encrypted endpoint."
                : "An HTTPS DNS endpoint must start with https://.";
        }
    }

    public ICommand SaveCommand { get; }
    public IAsyncRelayCommand ClearCredentialsCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand TestSteamCommand { get; }
    public IAsyncRelayCommand TestRyuuCommand { get; }
    public IAsyncRelayCommand TestHubcapCommand { get; }
    public IAsyncRelayCommand TestDepotBoxCommand { get; }
    public IAsyncRelayCommand TestDnsCommand { get; }
    public IAsyncRelayCommand TestMirrorCommand { get; }
    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public string AppVersionLabel =>
        $"You're on version {App.Services.GetRequiredService<IUpdateService>().CurrentVersion}. Updates come from github.com/{GitHubUpdateService.Repository}.";

    private string _updateStatus = string.Empty;
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = "Checking GitHub…";
        UpdateStatus = await App.CheckForUpdatesAsync();
    }

    // ---- saving ------------------------------------------------------------------------------

    /// <summary>Writes settings and any typed credentials; used by the button and by autosave.</summary>
    public async Task SaveAsync(string? note = null)
    {
        _autosaveCts?.Cancel();

        try
        {
            // Deliberately without ConfigureAwait(false): the status properties are bound to the
            // page and must be updated on the thread that owns them.
            await _settingsService.SaveAsync(Settings);
            await SaveCredentialsAsync();

            HasUnsavedChanges = false;
            SaveStatus = $"{note ?? "Saved"} · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            SaveStatus = $"Could not save the settings: {exception.Message}";
        }
    }

    /// <summary>
    /// Stores whatever was typed into the two password boxes. Empty boxes mean "leave the stored
    /// value alone", so saving the page never wipes a key the user did not retype.
    /// </summary>
    public async Task SaveCredentialsAsync()
    {
        var storedSomething = false;

        if (!string.IsNullOrWhiteSpace(SteamApiKeyInput))
        {
            await _credentials.SaveAsync(SteamApiKeyName, SteamApiKeyInput.Trim());
            storedSomething = true;
        }

        if (!string.IsNullOrWhiteSpace(RyuuAuthKeyInput))
        {
            await _credentials.SaveAsync(RyuuAuthKeyName, RyuuAuthKeyInput.Trim());
            storedSomething = true;
        }

        if (!string.IsNullOrWhiteSpace(HubcapApiKeyInput))
        {
            var trimmed = HubcapApiKeyInput.Trim();
            try { await _credentials.SaveAsync(HubcapApiKeyName, trimmed); } catch { }
            Settings.HubcapApiKey = trimmed;
            storedSomething = true;
        }

        if (!string.IsNullOrWhiteSpace(DepotBoxApiKeyInput))
        {
            var trimmed = DepotBoxApiKeyInput.Trim();
            try { await _credentials.SaveAsync(DepotBoxApiKeyName, trimmed); } catch { }
            Settings.DepotBoxApiKey = trimmed;
            storedSomething = true;
        }

        if (!string.IsNullOrWhiteSpace(MirrorTokenInput))
        {
            await _credentials.SaveAsync(MirrorTokenName, MirrorTokenInput.Trim());
            storedSomething = true;
        }

        if (!storedSomething) return;

        SteamApiKeyInput = string.Empty;
        RyuuAuthKeyInput = string.Empty;
        HubcapApiKeyInput = string.Empty;
        DepotBoxApiKeyInput = string.Empty;
        MirrorTokenInput = string.Empty;
        CredentialInputsCleared?.Invoke(this, EventArgs.Empty);
        await RefreshCredentialStatusAsync();
    }

    public async Task RefreshCredentialStatusAsync()
    {
        string? steam = null, ryuu = null, hubcap = null, depotBox = null, mirror = null;
        try { steam = await _credentials.ReadAsync(SteamApiKeyName); } catch { }
        try { ryuu = await _credentials.ReadAsync(RyuuAuthKeyName); } catch { }
        try { hubcap = await _credentials.ReadAsync(HubcapApiKeyName); } catch { }
        try { depotBox = await _credentials.ReadAsync(DepotBoxApiKeyName); } catch { }
        try { mirror = await _credentials.ReadAsync(MirrorTokenName); } catch { }

        SteamCredentialStatus = DescribeCredential(steam);
        RyuuCredentialStatus = DescribeCredential(ryuu);
        HubcapCredentialStatus = DescribeCredential(hubcap, Settings.HubcapApiKey);
        DepotBoxCredentialStatus = DescribeCredential(depotBox, Settings.DepotBoxApiKey);
        MirrorCredentialStatus = DescribeCredential(mirror);

        static string DescribeCredential(string? value, string? fallback = null)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return "Stored · encrypted with DPAPI";
            if (!string.IsNullOrWhiteSpace(fallback))
                return "Stored · app settings fallback";
            return "Not configured";
        }
    }

    private async Task ClearCredentialsAsync()
    {
        try { await _credentials.DeleteAsync(SteamApiKeyName); } catch { }
        try { await _credentials.DeleteAsync(RyuuAuthKeyName); } catch { }
        try { await _credentials.DeleteAsync(HubcapApiKeyName); } catch { }
        try { await _credentials.DeleteAsync(DepotBoxApiKeyName); } catch { }
        try { await _credentials.DeleteAsync(MirrorTokenName); } catch { }

        SteamApiKeyInput = string.Empty;
        RyuuAuthKeyInput = string.Empty;
        HubcapApiKeyInput = string.Empty;
        DepotBoxApiKeyInput = string.Empty;
        MirrorTokenInput = string.Empty;
        CredentialInputsCleared?.Invoke(this, EventArgs.Empty);

        await RefreshCredentialStatusAsync();
        SaveStatus = "Stored credentials were removed.";
    }

    private async Task ResetAsync()
    {
        _autosaveCts?.Cancel();
        await _settingsService.ResetAsync();

        Settings = _settingsService.Load();
        AttachSettings(Settings);
        SyncNumberFields();

        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SteamIdHint));
        OnPropertyChanged(nameof(DnsEndpointHint));

        SaveStatus = "Settings were reset to their defaults.";
        HasUnsavedChanges = false;
    }

    // ---- connection checks -------------------------------------------------------------------

    private async Task TestSteamAsync()
    {
        IsBusy = true;
        SteamTestStatus = "Checking the API endpoint…";

        try
        {
            var result = await _probe.ProbeAsync(Settings.SteamApiUrl);
            var key = string.IsNullOrWhiteSpace(await _credentials.ReadAsync(SteamApiKeyName))
                ? "No API key is stored yet."
                : "An API key is stored; this check never sends it.";

            SteamTestStatus = $"{result.Message} ({result.ElapsedMilliseconds} ms) {key}";
        }
        catch (Exception exception)
        {
            SteamTestStatus = $"The check failed: {exception.GetType().Name}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Turns an API error body into a short " — message" suffix for the status line.</summary>
    private static string ApiDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "detail", "message", "error" })
            {
                if (!doc.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                    continue;
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return $" — {text}";
            }
        }
        catch { }
        return string.Empty;
    }

    private async Task TestRyuuAsync()
    {
        // A key that was just typed is stored first, so the check never runs against an old value.
        await SaveCredentialsAsync();

        IsBusy = true;
        RyuuTestStatus = "Checking the generator endpoint…";

        try
        {
            var result = await _probe.ProbeAsync(Settings.RyuuBaseUrl);
            var key = string.IsNullOrWhiteSpace(await _credentials.ReadAsync(RyuuAuthKeyName))
                ? "No auth key is stored yet."
                : "An auth key is stored; this check never sends it.";

            RyuuTestStatus = $"{result.Message} ({result.ElapsedMilliseconds} ms) {key}";
        }
        catch (Exception exception)
        {
            RyuuTestStatus = $"The check failed: {exception.GetType().Name}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestHubcapAsync()
    {
        // A key that was just typed is stored first, so the check never runs against an old value.
        await SaveCredentialsAsync();

        IsBusy = true;
        HubcapTestStatus = "Checking Hubcap API…";
        HubcapUsageInfo = string.Empty;

        try
        {
            var baseUrl = Settings.HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
            string? key = null;
            try { key = await _credentials.ReadAsync(HubcapApiKeyName); } catch { }
            if (string.IsNullOrWhiteSpace(key))
                key = Settings.HubcapApiKey;

            var healthResult = await _probe.ProbeAsync($"{baseUrl}/api/v1/health");
            HubcapTestStatus = $"Health: {healthResult.Message} ({healthResult.ElapsedMilliseconds} ms)";

            if (!string.IsNullOrWhiteSpace(key))
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {key}");

                var resp = await http.GetAsync($"{baseUrl}/api/v1/user/stats");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var daily = root.TryGetProperty("daily_usage", out var du) ? du.GetInt32() : 0;
                    var limit = root.TryGetProperty("daily_limit", out var dl) ? dl.GetInt32() : 0;
                    HubcapUsageInfo = $"{daily} / {limit} requests today";
                    HubcapUsagePercent = limit > 0 ? (double)daily / limit * 100.0 : 0;
                    HubcapTestStatus += " · Key valid.";
                }
                else
                    HubcapTestStatus += $" · Key rejected: HTTP {(int)resp.StatusCode}{ApiDetail(await resp.Content.ReadAsStringAsync())}";
            }
            else
                HubcapTestStatus += " · No API key stored — type it into the box above.";
        }
        catch (Exception ex)
        {
            HubcapTestStatus = $"Check failed: {ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestDepotBoxAsync()
    {
        // A key that was just typed is stored first, so the check never runs against an old value.
        await SaveCredentialsAsync();

        IsBusy = true;
        DepotBoxTestStatus = "Checking DepotBox API…";
        DepotBoxUsageInfo = string.Empty;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

            // /api/stats is public, so it proves the service is reachable before the key is tested.
            var statsResponse = await http.GetAsync("https://depotbox.org/api/stats");
            if (statsResponse.IsSuccessStatusCode)
            {
                using var stats = JsonDocument.Parse(await statsResponse.Content.ReadAsStringAsync());
                var data = stats.RootElement.TryGetProperty("data", out var d) ? d : stats.RootElement;
                var tracked = data.TryGetProperty("tracked_games_db1", out var t) ? t.GetInt64() : 0;
                var requests = data.TryGetProperty("api_requests_last_24h", out var r) ? r.GetInt64() : 0;
                DepotBoxTestStatus = $"Online · {tracked:N0} games tracked · {requests:N0} API requests today";
            }
            else
            {
                DepotBoxTestStatus = $"depotbox.org answered HTTP {(int)statsResponse.StatusCode}";
            }

            string? key = null;
            try { key = await _credentials.ReadAsync(DepotBoxApiKeyName); } catch { }
            if (string.IsNullOrWhiteSpace(key))
                key = Settings.DepotBoxApiKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                DepotBoxTestStatus += " · No API key stored.";
                return;
            }

            // DepotBox accepts the key as X-API-Key and as a Bearer token — send both, exactly
            // like the download path does.
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", key);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            var usageResponse = await http.GetAsync("https://depotbox.org/api/usage/stats");
            if (usageResponse.IsSuccessStatusCode)
            {
                using var usage = JsonDocument.Parse(await usageResponse.Content.ReadAsStringAsync());
                if (usage.RootElement.TryGetProperty("stats", out var s))
                {
                    var allTime = s.TryGetProperty("allTime", out var a) ? a.GetInt64() : 0;
                    var last24h = s.TryGetProperty("last24h", out var l) ? l.GetInt64() : 0;
                    DepotBoxUsageInfo = $"{last24h:N0} requests in the last 24 h · {allTime:N0} total";
                }
                DepotBoxTestStatus += " · Key valid.";
            }
            else
            {
                DepotBoxTestStatus += $" · Key rejected: HTTP {(int)usageResponse.StatusCode}{ApiDetail(await usageResponse.Content.ReadAsStringAsync())}";
            }
        }
        catch (Exception ex)
        {
            DepotBoxTestStatus = $"Check failed: {ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestMirrorAsync()
    {
        // A token that was just typed is stored first, so the check never runs against an old value.
        await SaveCredentialsAsync();

        IsBusy = true;
        MirrorTestStatus = "Checking the fixes source…";

        try
        {
            var source = FixSource.Resolve(Settings.FixMirrorUrl);
            if (source is null)
            {
                MirrorTestStatus = "That URL is not valid. Use a GitHub repository URL (https://github.com/owner/repo) or leave it empty for the built-in source.";
                return;
            }

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");
            var token = await _credentials.ReadAsync(MirrorTokenName);
            if (!string.IsNullOrWhiteSpace(token))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);

            using var feedResponse = await http.GetAsync(source.FeedUrl);
            if (!feedResponse.IsSuccessStatusCode)
            {
                MirrorTestStatus = $"fixes.json: HTTP {(int)feedResponse.StatusCode} — check the URL and token.";
                return;
            }

            using var catalog = JsonDocument.Parse(await feedResponse.Content.ReadAsStringAsync());
            var games = catalog.RootElement.ValueKind == JsonValueKind.Array ? catalog.RootElement.GetArrayLength() : 0;
            var sample = FirstFixEntry(catalog.RootElement);
            if (sample is null)
            {
                MirrorTestStatus = $"fixes.json OK ({games:N0} games), but it lists no archives.";
                return;
            }

            using var archiveResponse = await http.GetAsync(source.FileUrl(sample), System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            MirrorTestStatus = archiveResponse.IsSuccessStatusCode
                ? $"Source OK · {games:N0} games · archive downloads work."
                : $"fixes.json OK ({games:N0} games), but archives return HTTP {(int)archiveResponse.StatusCode}.";
        }
        catch (Exception ex)
        {
            MirrorTestStatus = $"Check failed: {ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static FixEntry? FirstFixEntry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        foreach (var game in root.EnumerateArray())
        {
            if (!game.TryGetProperty("fixes", out var fixes) || fixes.ValueKind != JsonValueKind.Array) continue;
            foreach (var fix in fixes.EnumerateArray())
            {
                var path = fix.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var file = fix.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                if (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(file))
                    return new FixEntry { Path = path ?? string.Empty, Filename = file ?? string.Empty };
            }
        }
        return null;
    }

    private async Task TestDnsAsync()
    {
        IsBusy = true;
        DnsStatus = "Resolving…";

        try
        {
            var result = await _dns.DiagnoseAsync(Settings.DnsTestHost, Settings.DnsMode, Settings.DnsEndpoint);

            DnsAddresses = result.Addresses.Count == 0 ? "—" : string.Join(", ", result.Addresses);
            DnsLatency = $"{result.ElapsedMilliseconds} ms";
            DnsStatus = result.Message;
        }
        catch (Exception exception)
        {
            DnsStatus = $"DNS diagnostic failed: {exception.GetType().Name}.";
            DnsAddresses = "—";
            DnsLatency = "—";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyDnsMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;

        Settings.DnsMode = mode;
        if (DnsEndpointFor(mode) is { } endpoint) Settings.DnsEndpoint = endpoint;

        DnsStatus = $"{mode} selected.";
        OnPropertyChanged(nameof(DnsEndpointHint));
        OnPropertyChanged(nameof(Settings));
    }

    public static string? DnsEndpointFor(string? mode) => mode switch
    {
        "Cloudflare DoH" => "https://cloudflare-dns.com/dns-query",
        "Google DoH" => "https://dns.google/dns-query",
        "Quad9 DoH" => "https://dns.quad9.net/dns-query",
        _ => null
    };

    /// <summary>Safe export: settings only, never the stored credentials.</summary>
    public Task ExportAsync(string path, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

    // ---- autosave ----------------------------------------------------------------------------

    /// <summary>
    /// Watches the settings object. Any change schedules one write shortly afterwards, so a burst of
    /// edits costs a single file write and nothing is lost when the page is left without pressing
    /// Save.
    /// </summary>
    private void AttachSettings(AppSettings settings)
    {
        // A replaced settings object (reset) must not keep queuing saves for the abandoned one.
        if (_settingsChangedHandler is not null && Settings is not null)
            Settings.PropertyChanged -= _settingsChangedHandler;

        _settingsChangedHandler = (_, _) =>
        {
            HasUnsavedChanges = true;
            SaveStatus = "Unsaved changes…";
            OnPropertyChanged(nameof(SteamIdHint));
            OnPropertyChanged(nameof(DnsEndpointHint));
            QueueAutosave();
        };

        settings.PropertyChanged += _settingsChangedHandler;
    }

    private void QueueAutosave()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = new CancellationTokenSource();
        _ = AutosaveAfterDelayAsync(_autosaveCts.Token);
    }

    /// <summary>
    /// Waits for the user to stop editing and then saves. The delay is awaited on the calling
    /// context, so the save and the status update happen on the UI thread that owns the bindings.
    /// </summary>
    private async Task AutosaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(AutosaveDelay, token);
            if (token.IsCancellationRequested) return;
            await SaveAsync("Saved automatically");
        }
        catch (TaskCanceledException)
        {
            // A newer change is on its way; that one will be saved instead.
        }
    }

    /// <summary>Leaves the page: pending changes are written immediately.</summary>
    public override async Task OnNavigatedFromAsync()
    {
        // A key typed moments before leaving must not be lost to the debounce.
        _credentialAutosaveCts?.Cancel();
        await SaveCredentialsAsync();
        if (HasUnsavedChanges) await SaveAsync("Saved when leaving the page");
        await base.OnNavigatedFromAsync();
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void ApplyNumber(string text, int min, int max, Action<int> assign, Action<string> setHint, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            setHint($"Enter a number ({min}–{max} {what}).");
            return;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            setHint($"“{text.Trim()}” is not a number.");
            return;
        }

        var clamped = Math.Clamp(value, min, max);
        assign(clamped);

        setHint(clamped == value
            ? $"Set to {clamped}."
            : $"Adjusted to {clamped}; the allowed range is {min}–{max}.");
    }

    /// <summary>Mirrors the stored numbers into the text boxes, e.g. after a reset.</summary>
    private void SyncNumberFields()
    {
        ParallelDownloadsText = Settings.ParallelDownloads.ToString(CultureInfo.InvariantCulture);
        RetryCountText = Settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        TimeoutSecondsText = Settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        DownloadConnectionsText = Settings.DownloadConnections.ToString(CultureInfo.InvariantCulture);

        DownloadConnectionsHint = $"1–{DepotDownloaderArgumentBuilder.MaxDownloadsLimit} connections per job (current: {Settings.DownloadConnections}).";
        ParallelDownloadsHint = $"1–16 parallel jobs (current: {Settings.ParallelDownloads}).";
        RetryCountHint = $"0–10 retries (current: {Settings.RetryCount}).";
        TimeoutSecondsHint = $"5–3600 seconds (current: {Settings.TimeoutSeconds}).";
    }

    /// <summary>Called when a number box loses focus: shows the value that is really stored.</summary>
    public void NormalizeNumberFields() => SyncNumberFields();
}
