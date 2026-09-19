using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

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

    private const string SteamApiKeyName = "steam-api-key";
    private const string RyuuAuthKeyName = "ryuu-auth-key";

    private readonly ISettingsService _settingsService;
    private readonly ISecureCredentialService _credentials;
    private readonly IDnsResolverService _dns;
    private readonly IEndpointProbeService _probe;

    private CancellationTokenSource? _autosaveCts;
    private System.ComponentModel.PropertyChangedEventHandler? _settingsChangedHandler;
    private string _status = "Changes are saved automatically.";
    private string _steamCredentialStatus = "Not configured";
    private string _ryuuCredentialStatus = "Not configured";
    private string _steamTestStatus = "Not checked yet.";
    private string _ryuuTestStatus = "Not checked yet.";
    private string _dnsStatus = "App-only DNS diagnostics are ready.";
    private string _dnsAddresses = "—";
    private string _dnsLatency = "—";
    private string _parallelText = string.Empty;
    private string _retryText = string.Empty;
    private string _timeoutText = string.Empty;
    private string _parallelHint = string.Empty;
    private string _retryHint = string.Empty;
    private string _timeoutHint = string.Empty;
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
        TestDnsCommand = new AsyncRelayCommand(TestDnsAsync);

        _ = RefreshCredentialStatusAsync();
    }

    /// <summary>Raised after credentials were stored or deleted so the page can clear its boxes.</summary>
    public event EventHandler? CredentialInputsCleared;

    public AppSettings Settings { get; private set; }

    public string[] Languages { get; } = { "System Default", "Deutsch", "English" };
    public string[] Appearances { get; } = { "System", "Light", "Dark" };
    public string[] DnsModes { get; } = { "System resolver", "Cloudflare DoH", "Google DoH", "Quad9 DoH", "Custom DoH" };

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string SteamApiKeyInput { get; set; } = string.Empty;

    /// <summary>Bound to the password box; only ever written into the encrypted store.</summary>
    public string RyuuAuthKeyInput { get; set; } = string.Empty;

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
    public IAsyncRelayCommand TestDnsCommand { get; }

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

        if (!storedSomething) return;

        // The plain text is not kept around once it has been written to the encrypted store.
        SteamApiKeyInput = string.Empty;
        RyuuAuthKeyInput = string.Empty;
        CredentialInputsCleared?.Invoke(this, EventArgs.Empty);
        await RefreshCredentialStatusAsync();
    }

    public async Task RefreshCredentialStatusAsync()
    {
        var steam = await _credentials.ReadAsync(SteamApiKeyName);
        var ryuu = await _credentials.ReadAsync(RyuuAuthKeyName);

        SteamCredentialStatus = DescribeCredential(steam);
        RyuuCredentialStatus = DescribeCredential(ryuu);

        static string DescribeCredential(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? "Not configured"
                : "Stored · encrypted with DPAPI";
    }

    private async Task ClearCredentialsAsync()
    {
        await _credentials.DeleteAsync(SteamApiKeyName);
        await _credentials.DeleteAsync(RyuuAuthKeyName);

        SteamApiKeyInput = string.Empty;
        RyuuAuthKeyInput = string.Empty;
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

    private async Task TestRyuuAsync()
    {
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

        ParallelDownloadsHint = $"1–16 parallel jobs (current: {Settings.ParallelDownloads}).";
        RetryCountHint = $"0–10 retries (current: {Settings.RetryCount}).";
        TimeoutSecondsHint = $"5–3600 seconds (current: {Settings.TimeoutSeconds}).";
    }

    /// <summary>Called when a number box loses focus: shows the value that is really stored.</summary>
    public void NormalizeNumberFields() => SyncNumberFields();
}
