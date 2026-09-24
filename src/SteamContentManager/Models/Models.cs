using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;

namespace SteamContentManager.Models;

public enum DownloadJobState
{
    Queued,
    Preparing,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
    Paused
}

public enum DownloadPriority
{
    Low,
    Normal,
    High
}

public enum ProviderConnectionState
{
    NotConfigured,
    Healthy,
    Degraded,
    Offline,
    Error
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public enum GameInstallState
{
    Installed,
    NotInstalled,
    Updating
}

public sealed class Game : UiObservableObject
{
    private DownloadJobState? _currentState;
    private BitmapImage? _artworkImage;
    private BitmapImage? _headerImage;
    private bool _isArtworkLoading;
    private bool _selected;

    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string CoverColor { get; init; } = "#242833";
    public string CoverGlyph { get; init; } = "◆";
    public string InstallFolder { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public long SizeOnDiskBytes { get; init; }
    public GameInstallState InstallState { get; init; }
    public bool UpdateRequired { get; init; }

    /// <summary>Selection in the library list; the queue accepts all selected entries.</summary>
    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
    public string LastPlayed { get; init; } = string.Empty;
    public DateTime? LastUpdated { get; init; }
    public string DepotSummary { get; init; } = string.Empty;
    public string ManifestSummary { get; init; } = string.Empty;
    public int AchievementPercent { get; init; }
    public string AppIdLabel => $"App ID {AppId}";
    public string AppLabel => $"APP {AppId}";
    public string AchievementLabel => $"{AchievementPercent}% achievements";
    public string ArtworkUrl => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/library_600x900_2x.jpg";
    public string HeaderArtworkUrl => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

    public BitmapImage? ArtworkImage
    {
        get => _artworkImage;
        set
        {
            if (SetProperty(ref _artworkImage, value)) OnPropertyChanged(nameof(IsArtworkFallback));
        }
    }

    public BitmapImage? HeaderImage
    {
        get => _headerImage;
        set
        {
            if (SetProperty(ref _headerImage, value)) OnPropertyChanged(nameof(IsHeaderArtworkFallback));
        }
    }

    public bool IsArtworkLoading
    {
        get => _isArtworkLoading;
        set => SetProperty(ref _isArtworkLoading, value);
    }

    public bool IsArtworkFallback => ArtworkImage is null;
    public bool IsHeaderArtworkFallback => HeaderImage is null;

    public DownloadJobState? CurrentState
    {
        get => _currentState;
        set => SetProperty(ref _currentState, value);
    }

    public string InstallStateLabel => InstallState switch
    {
        GameInstallState.Installed => "Installed",
        GameInstallState.Updating => "Updating",
        _ => "Not installed"
    };
}

public sealed class DownloadJob : UiObservableObject
{
    private DownloadJobState _state;
    private double _progress;
    private string _status = string.Empty;
    private DownloadPriority _priority;
    private string _downloaded = string.Empty;
    private string _totalSize = string.Empty;
    private string _speed = string.Empty;
    private string _diskSpeed = string.Empty;
    private string _eta = string.Empty;
    private string _currentFile = string.Empty;
    private string _processLog = string.Empty;
    private int? _exitCode;
    private int? _depotId;
    private string _branch = "public";
    private string _manifestId = string.Empty;
    private bool _authorizationConfirmed;
    private string _downloadMode = "Demo fallback";

    public Guid Id { get; init; } = Guid.NewGuid();
    public int AppId { get; init; }
    public string GameName { get; init; } = string.Empty;
    public string CoverColor { get; init; } = "#242833";
    public string CoverGlyph { get; init; } = "◆";
    public string CoverImageUrl { get; init; } = string.Empty;
    public string TargetFolder { get; init; } = string.Empty;
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }

    public int? DepotId
    {
        get => _depotId;
        init => _depotId = value;
    }

    public string Branch
    {
        get => _branch;
        init => _branch = string.IsNullOrWhiteSpace(value) ? "public" : value;
    }

    public string ManifestId
    {
        get => _manifestId;
        init => _manifestId = value ?? string.Empty;
    }

    public bool AuthorizationConfirmed
    {
        get => _authorizationConfirmed;
        init => _authorizationConfirmed = value;
    }

    public string DownloadMode
    {
        get => _downloadMode;
        set => SetProperty(ref _downloadMode, value);
    }

    public string Downloaded
    {
        get => _downloaded;
        set
        {
            if (SetProperty(ref _downloaded, value)) OnPropertyChanged(nameof(SizeSummary));
        }
    }

    public string TotalSize
    {
        get => _totalSize;
        set
        {
            if (SetProperty(ref _totalSize, value)) OnPropertyChanged(nameof(SizeSummary));
        }
    }

    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    public string DiskSpeed
    {
        get => _diskSpeed;
        set => SetProperty(ref _diskSpeed, value);
    }

    public string Eta
    {
        get => _eta;
        set => SetProperty(ref _eta, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    public string ProcessLog
    {
        get => _processLog;
        set => SetProperty(ref _processLog, value);
    }

    public int? ExitCode
    {
        get => _exitCode;
        set
        {
            if (SetProperty(ref _exitCode, value)) OnPropertyChanged(nameof(ExitCodeLabel));
        }
    }

    public string SizeSummary =>
        string.IsNullOrWhiteSpace(Downloaded) || Downloaded == "—"
            ? (string.IsNullOrWhiteSpace(TotalSize) ? string.Empty : TotalSize)
            : string.IsNullOrWhiteSpace(TotalSize) ? Downloaded : $"{Downloaded} / {TotalSize}";
    public string AppLabel => $"App {AppId}";
    public string DepotLabel => DepotId is null ? "App depot set" : $"Depot {DepotId}";
    public string ExitCodeLabel => ExitCode is null ? "—" : ExitCode.Value.ToString();
    public string? PortraitArtUrl => AppId > 0 ? $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/library_600x900.jpg" : null;
    public bool IsPaused => State == DownloadJobState.Paused;
    public bool IsTerminal => State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled;

    public DownloadJobState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsTerminal));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, Math.Clamp(value, 0, 100)))
                OnPropertyChanged(nameof(ProgressLabel));
        }
    }

    public string ProgressLabel => $"{Progress:0.#}%";

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public DownloadPriority Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public string StateLabel => State switch
    {
        DownloadJobState.Downloading => "Downloading",
        DownloadJobState.Queued => "Queued",
        DownloadJobState.Preparing => "Preparing",
        DownloadJobState.Verifying => "Verifying",
        DownloadJobState.Completed => "Completed",
        DownloadJobState.Failed => "Failed",
        DownloadJobState.Cancelled => "Cancelled",
        DownloadJobState.Paused => "Paused",
        _ => State.ToString()
    };

    public bool IsActive => State is DownloadJobState.Downloading or DownloadJobState.Preparing or DownloadJobState.Verifying;

    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        ProcessLog = string.IsNullOrWhiteSpace(ProcessLog) ? line : $"{ProcessLog}{Environment.NewLine}{line}";
    }
}

public sealed class Depot : ObservableObject
{
    private bool _selected;

    public int AppId { get; init; }
    public int DepotId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Branch { get; init; } = "public";
    public string ManifestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Updated { get; init; } = string.Empty;

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

public sealed class Manifest : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int AppId { get; init; }
    public int DepotId { get; init; }
    public string Size { get; init; } = string.Empty;
    public string ManifestId { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string ValidationStatus { get; set; } = string.Empty;
    public DateTime ImportedAt { get; init; }
    public string ImportedAtLabel => ImportedAt.ToString("dd.MM.yyyy HH:mm");
}

public sealed class Branch : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Build { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string LastChecked { get; init; } = string.Empty;
    public string RequestStatus { get; init; } = string.Empty;
}

public sealed class Achievement : ObservableObject
{
    public int AppId { get; init; }
    public string GameName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "★";
    public bool Unlocked { get; init; }
    public int Progress { get; init; }
    public string UnlockedAt { get; init; } = string.Empty;
    public string ProgressLabel => $"{Progress}% progress";
}

public sealed class ContentProvider : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public ProviderConnectionState State { get; init; }
    public string Authentication { get; init; } = string.Empty;
    public string RateLimit { get; init; } = string.Empty;
    public string LastRequest { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;

    public string StateLabel => State switch
    {
        ProviderConnectionState.Healthy => "Usable",
        ProviderConnectionState.Degraded => "Degraded",
        ProviderConnectionState.Offline => "Offline",
        ProviderConnectionState.Error => "Error",
        _ => "Not configured"
    };
}

public sealed class ModFix : ObservableObject
{
    private string _status = string.Empty;
    private string _localFolder = string.Empty;
    private string _backupFolder = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int GameAppId { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Compatibility { get; init; } = string.Empty;
    public string VersionLabel => $"v{Version}";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool BackupRequired { get; init; }
    public string LocalFolder { get => _localFolder; set => SetProperty(ref _localFolder, value); }
    public string BackupFolder { get => _backupFolder; set => SetProperty(ref _backupFolder, value); }
}

public sealed class GenerationTemplate : ObservableObject
{
    private string _status = string.Empty;
    private string _lastGenerated = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string OutputFormat { get; init; } = string.Empty;
    public bool IsMockOnly { get; init; }
    public string LastGenerated { get; set; } = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
}

public sealed class LogEntry : ObservableObject
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Component { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? AppId { get; init; }
    public Guid? JobId { get; init; }

    public string LevelLabel => Level.ToString().ToUpperInvariant();
    public string TimestampLabel => Timestamp.ToString("dd.MM.yyyy HH:mm:ss");
    public string AppIdLabel => AppId?.ToString() ?? "—";
    public string JobIdLabel => JobId?.ToString() ?? "—";
}

public sealed class GameFixItem : ObservableObject
{
    private bool _selected;
    private string _status = "Not downloaded";
    private string _localPath = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string GameName { get; init; } = string.Empty;
    public int AppId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string AppOrFileLabel => string.IsNullOrWhiteSpace(AppLabel) ? $"File {FileName}" : AppLabel;
    public string AppLabel => $"App {AppId}";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
    public bool IsDownloaded => Status is "Downloaded" or "Applied";
    public bool IsApplied => Status == "Applied";
}

public sealed class AppSettings : ObservableObject
{
    private string _language = "System Default";
    private string _appearance = "Dark";
    private bool _autoUpdate = true;
    private bool _notifications = true;
    private bool _autoRefresh = true;
    private bool _confirmBeforeDelete = true;
    private string _steamApiUrl = "https://api.steampowered.com";
    private string _steamId64 = string.Empty;
    private string _ryuuBaseUrl = "https://generator.ryuu.lol/";
    private string _steamLibraryPath = string.Empty;
    private string _depotDownloaderPath = string.Empty;
    private string _steamUsername = string.Empty;
    private bool _interactiveToolConsole;
    private string _workingDirectory = string.Empty;
    private string _downloadFolder = string.Empty;
    private int _parallelDownloads = 2;
    private int _retryCount = 3;
    private int _timeoutSeconds = 60;
    private bool _verifyAfterDownload = true;
    private bool _keepHistory = true;
    private bool _autoResume = true;
    private bool _debugLogging;
    private bool _showDownloadLogs;
    private string _dnsMode = "System resolver";
    private string _dnsEndpoint = "https://cloudflare-dns.com/dns-query";
    private string _dnsTestHost = "api.steampowered.com";

    private string _steamlessExePath = string.Empty;
    private string _steamlessTargetExePath = string.Empty;
    private string _steamlessExtraArguments = string.Empty;
    private string _creamInstallerPath = string.Empty;
    private string _creamInstallerTargetExePath = string.Empty;
    private string _creamInstallerSteamApiPath = string.Empty;
    private string _xStoreUnlockerPath = string.Empty;
    private string _ryuuApiKey = string.Empty;
    private string _denuvoGeneratorPath = string.Empty;
    private string _denuvoSteamApiHint = string.Empty;
    private string _unsteamPath = string.Empty;
    private string _screamApiPath = string.Empty;
    private string _hvFixesPath = string.Empty;
    private string _creamApiProxy = string.Empty;
    private string _backdropStyle = "Mica";
    private string _hubcapBaseUrl = "https://hubcapmanifest.com";
    private string _fixMirrorUrl = string.Empty;

    public string CreamApiProxy { get => _creamApiProxy; set => SetProperty(ref _creamApiProxy, value); }
    public string BackdropStyle { get => _backdropStyle; set => SetProperty(ref _backdropStyle, value); }
    public string HubcapBaseUrl { get => _hubcapBaseUrl; set => SetProperty(ref _hubcapBaseUrl, value); }
    public string FixMirrorUrl { get => _fixMirrorUrl; set => SetProperty(ref _fixMirrorUrl, value); }

    public string RyuuApiKey { get => _ryuuApiKey; set => SetProperty(ref _ryuuApiKey, value); }

    public string Language { get => _language; set => SetProperty(ref _language, value); }
    public string Appearance { get => _appearance; set => SetProperty(ref _appearance, value); }
    public bool AutoUpdate { get => _autoUpdate; set => SetProperty(ref _autoUpdate, value); }
    public bool Notifications { get => _notifications; set => SetProperty(ref _notifications, value); }
    public bool AutoRefresh { get => _autoRefresh; set => SetProperty(ref _autoRefresh, value); }
    public bool ConfirmBeforeDelete { get => _confirmBeforeDelete; set => SetProperty(ref _confirmBeforeDelete, value); }
    public string SteamApiUrl { get => _steamApiUrl; set => SetProperty(ref _steamApiUrl, value); }
    public string SteamId64 { get => _steamId64; set => SetProperty(ref _steamId64, value); }
    public string RyuuBaseUrl { get => _ryuuBaseUrl; set => SetProperty(ref _ryuuBaseUrl, value); }
    public string SteamLibraryPath { get => _steamLibraryPath; set => SetProperty(ref _steamLibraryPath, value); }
    public string DepotDownloaderPath { get => _depotDownloaderPath; set => SetProperty(ref _depotDownloaderPath, value); }


    public string SteamlessExePath { get => _steamlessExePath; set => SetProperty(ref _steamlessExePath, value); }
    public string SteamlessTargetExePath { get => _steamlessTargetExePath; set => SetProperty(ref _steamlessTargetExePath, value); }
    public string SteamlessExtraArguments { get => _steamlessExtraArguments; set => SetProperty(ref _steamlessExtraArguments, value); }
    public string CreamInstallerPath { get => _creamInstallerPath; set => SetProperty(ref _creamInstallerPath, value); }
    public string CreamInstallerTargetExePath { get => _creamInstallerTargetExePath; set => SetProperty(ref _creamInstallerTargetExePath, value); }
    public string CreamInstallerSteamApiPath { get => _creamInstallerSteamApiPath; set => SetProperty(ref _creamInstallerSteamApiPath, value); }
    public string XStoreUnlockerPath { get => _xStoreUnlockerPath; set => SetProperty(ref _xStoreUnlockerPath, value); }
    public string DenuvoGeneratorPath { get => _denuvoGeneratorPath; set => SetProperty(ref _denuvoGeneratorPath, value); }
    public string DenuvoSteamApiHint { get => _denuvoSteamApiHint; set => SetProperty(ref _denuvoSteamApiHint, value); }
    public string UnsteamPath { get => _unsteamPath; set => SetProperty(ref _unsteamPath, value); }
    public string ScreamApiPath { get => _screamApiPath; set => SetProperty(ref _screamApiPath, value); }
    public string HvFixesPath { get => _hvFixesPath; set => SetProperty(ref _hvFixesPath, value); }

    /// <summary>Optional Steam account name passed to the tool as <c>-username</c>. Never a password.</summary>
    public string SteamUsername { get => _steamUsername; set => SetProperty(ref _steamUsername, value); }

    /// <summary>Lets DepotDownloader keep its own console window so it can ask for a password or Steam Guard code.</summary>
    public bool InteractiveToolConsole { get => _interactiveToolConsole; set => SetProperty(ref _interactiveToolConsole, value); }

    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public string DownloadFolder { get => _downloadFolder; set => SetProperty(ref _downloadFolder, value); }
    public int ParallelDownloads { get => _parallelDownloads; set => SetProperty(ref _parallelDownloads, value); }
    public int RetryCount { get => _retryCount; set => SetProperty(ref _retryCount, value); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }
    public bool VerifyAfterDownload { get => _verifyAfterDownload; set => SetProperty(ref _verifyAfterDownload, value); }
    public bool KeepHistory { get => _keepHistory; set => SetProperty(ref _keepHistory, value); }
    public bool AutoResume { get => _autoResume; set => SetProperty(ref _autoResume, value); }
    public bool DebugLogging { get => _debugLogging; set => SetProperty(ref _debugLogging, value); }
    public bool ShowDownloadLogs { get => _showDownloadLogs; set => SetProperty(ref _showDownloadLogs, value); }
    public string DnsMode { get => _dnsMode; set => SetProperty(ref _dnsMode, value); }
    public string DnsEndpoint { get => _dnsEndpoint; set => SetProperty(ref _dnsEndpoint, value); }
    public string DnsTestHost { get => _dnsTestHost; set => SetProperty(ref _dnsTestHost, value); }
}
