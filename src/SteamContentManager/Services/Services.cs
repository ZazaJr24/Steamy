using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public interface INavigationService
{
    void Attach(Action<Type> navigate);
    void Detach();
    void Navigate<TPage>();
}

public sealed class NavigationService : INavigationService
{
    private Action<Type>? _navigate;

    public void Attach(Action<Type> navigate) => _navigate = navigate;
    public void Detach() => _navigate = null;
    public void Navigate<TPage>() => _navigate?.Invoke(typeof(TPage));
}

public interface IAppDataStore
{
    ObservableCollection<Game> Games { get; }
    ObservableCollection<DownloadJob> Downloads { get; }
    ObservableCollection<Depot> Depots { get; }
    ObservableCollection<Manifest> Manifests { get; }
    ObservableCollection<Branch> Branches { get; }
    ObservableCollection<Achievement> Achievements { get; }
    ObservableCollection<ContentProvider> Providers { get; }
    ObservableCollection<ModFix> ModFixes { get; }
    ObservableCollection<GenerationTemplate> GenerationTemplates { get; }
    ObservableCollection<LogEntry> Logs { get; }
}

public sealed class DemoDataStore : IAppDataStore
{
    public ObservableCollection<Game> Games { get; } = new()
    {
        new Game { AppId = 730, Name = "Counter-Strike 2", ShortName = "CS2", CoverColor = "#384F82", CoverGlyph = "◈", InstallFolder = "D:\\SteamLibrary\\steamapps\\common\\Counter-Strike Global Offensive", Size = "35.8 GB", InstallState = GameInstallState.Installed, LastPlayed = "Today, 18:42", DepotSummary = "4 depots available", ManifestSummary = "Manifest verified", AchievementPercent = 72 },
        new Game { AppId = 570, Name = "Dota 2", ShortName = "DOTA", CoverColor = "#6A3438", CoverGlyph = "✦", InstallFolder = "D:\\SteamLibrary\\steamapps\\common\\dota 2 beta", Size = "46.2 GB", InstallState = GameInstallState.Installed, LastPlayed = "Yesterday", DepotSummary = "6 depots available", ManifestSummary = "Manifest verified", AchievementPercent = 48 },
        new Game { AppId = 1172470, Name = "Apex Legends", ShortName = "APEX", CoverColor = "#7B3B2E", CoverGlyph = "△", InstallFolder = string.Empty, Size = "72.4 GB", InstallState = GameInstallState.NotInstalled, LastPlayed = "Last week", DepotSummary = "3 depots available", ManifestSummary = "Not imported", AchievementPercent = 31 },
        new Game { AppId = 1245620, Name = "Elden Ring", ShortName = "ELDEN", CoverColor = "#5B4A32", CoverGlyph = "✧", InstallFolder = "E:\\Games\\Elden Ring", Size = "61.1 GB", InstallState = GameInstallState.Updating, LastPlayed = "Mar 05", DepotSummary = "5 depots available", ManifestSummary = "Manifest pending", AchievementPercent = 64 },
        new Game { AppId = 1086940, Name = "Baldur's Gate 3", ShortName = "BG3", CoverColor = "#513A64", CoverGlyph = "☽", InstallFolder = string.Empty, Size = "142.8 GB", InstallState = GameInstallState.NotInstalled, LastPlayed = "Feb 27", DepotSummary = "2 depots available", ManifestSummary = "Not imported", AchievementPercent = 18 },
        new Game { AppId = 271590, Name = "Grand Theft Auto V", ShortName = "GTA V", CoverColor = "#376A59", CoverGlyph = "★", InstallFolder = "D:\\SteamLibrary\\steamapps\\common\\Grand Theft Auto V", Size = "110.3 GB", InstallState = GameInstallState.Installed, LastPlayed = "Feb 19", DepotSummary = "8 depots available", ManifestSummary = "Manifest verified", AchievementPercent = 86 }
    };

    public ObservableCollection<DownloadJob> Downloads { get; } = new()
    {
        new DownloadJob { AppId = 1245620, GameName = "Elden Ring", CoverColor = "#5B4A32", CoverGlyph = "✧", State = DownloadJobState.Downloading, Progress = 72, Status = "Downloading content", Downloaded = "42.1 GB", TotalSize = "58.0 GB", Speed = "18.4 MB/s", Eta = "14 min remaining", CurrentFile = "eldenring.exe", TargetFolder = "E:\\Games\\Elden Ring", Started = DateTime.Now.AddMinutes(-31), Priority = DownloadPriority.High },
        new DownloadJob { AppId = 1086940, GameName = "Baldur's Gate 3", CoverColor = "#513A64", CoverGlyph = "☽", State = DownloadJobState.Queued, Progress = 0, Status = "Waiting for slot", Downloaded = "0 B", TotalSize = "142.8 GB", Speed = "—", Eta = "Queued", CurrentFile = "—", TargetFolder = "E:\\Games\\Baldur's Gate 3", Started = DateTime.Now, Priority = DownloadPriority.Normal },
        new DownloadJob { AppId = 730, GameName = "Counter-Strike 2", CoverColor = "#384F82", CoverGlyph = "◈", State = DownloadJobState.Completed, Progress = 100, Status = "Verified successfully", Downloaded = "35.8 GB", TotalSize = "35.8 GB", Speed = "—", Eta = "Completed today", CurrentFile = "—", TargetFolder = "D:\\SteamLibrary\\steamapps\\common\\Counter-Strike Global Offensive", Started = DateTime.Now.AddHours(-5), Finished = DateTime.Now.AddHours(-4), Priority = DownloadPriority.Normal }
    };

    public ObservableCollection<Depot> Depots { get; } = new()
    {
        new Depot { AppId = 730, DepotId = 730, Name = "Counter-Strike 2 Content", Size = "35.8 GB", Branch = "public", ManifestId = "184783902114", Status = "Installed", Updated = "Today" , Selected = true},
        new Depot { AppId = 730, DepotId = 234777, Name = "Windows Binaries", Size = "14.2 GB", Branch = "public", ManifestId = "184783902115", Status = "Verified", Updated = "Today", Selected = true },
        new Depot { AppId = 1245620, DepotId = 1245621, Name = "Elden Ring Base", Size = "52.4 GB", Branch = "public", ManifestId = "99124882214", Status = "Available", Updated = "2 days ago", Selected = true },
        new Depot { AppId = 1245620, DepotId = 1245622, Name = "Elden Ring Language Pack", Size = "5.6 GB", Branch = "public", ManifestId = "99124882215", Status = "Available", Updated = "2 days ago" },
        new Depot { AppId = 1086940, DepotId = 1086941, Name = "Baldur's Gate 3 Windows", Size = "142.8 GB", Branch = "public", ManifestId = "—", Status = "Not imported", Updated = "—" }
    };

    public ObservableCollection<Manifest> Manifests { get; } = new()
    {
        new Manifest { FileName = "app_730_depot_730.manifest", Path = "D:\\Manifests\\app_730_depot_730.manifest", AppId = 730, DepotId = 730, Size = "42 KB", ManifestId = "184783902114", Hash = "a9f3…7c21", ValidationStatus = "Valid", ImportedAt = DateTime.Now.AddHours(-3) },
        new Manifest { FileName = "app_730_depot_234777.manifest", Path = "D:\\Manifests\\app_730_depot_234777.manifest", AppId = 730, DepotId = 234777, Size = "18 KB", ManifestId = "184783902115", Hash = "7b11…29ad", ValidationStatus = "Valid", ImportedAt = DateTime.Now.AddDays(-1) },
        new Manifest { FileName = "app_1245620_depot_1245621.manifest", Path = "E:\\Manifests\\elden_ring.manifest", AppId = 1245620, DepotId = 1245621, Size = "64 KB", ManifestId = "99124882214", Hash = "pending", ValidationStatus = "Needs validation", ImportedAt = DateTime.Now.AddDays(-2) }
    };

    public ObservableCollection<Branch> Branches { get; } = new()
    {
        new Branch { Name = "public", Type = "Public", Build = "1847839", Status = "Available", LastChecked = "Just now", RequestStatus = "Ready" },
        new Branch { Name = "beta", Type = "Beta", Build = "1846120", Status = "Available", LastChecked = "Today, 11:20", RequestStatus = "Ready" },
        new Branch { Name = "previous_version", Type = "Authorized branch", Build = "1810022", Status = "Available", LastChecked = "Yesterday", RequestStatus = "Ready" },
        new Branch { Name = "experimental", Type = "Authorized branch", Build = "—", Status = "Not available", LastChecked = "Never", RequestStatus = "Not requested" }
    };

    public ObservableCollection<Achievement> Achievements { get; } = new()
    {
        new Achievement { AppId = 730, GameName = "Counter-Strike 2", Name = "First Steps", Description = "Complete your first match.", IconGlyph = "✦", Unlocked = true, Progress = 100, UnlockedAt = "Today" },
        new Achievement { AppId = 730, GameName = "Counter-Strike 2", Name = "Team Player", Description = "Win a match with your team.", IconGlyph = "◆", Unlocked = true, Progress = 100, UnlockedAt = "Yesterday" },
        new Achievement { AppId = 1245620, GameName = "Elden Ring", Name = "Erdtree Aflame", Description = "Use kindling to set the Erdtree aflame.", IconGlyph = "♨", Unlocked = false, Progress = 68, UnlockedAt = "Locked" },
        new Achievement { AppId = 1086940, GameName = "Baldur's Gate 3", Name = "A Little Helper", Description = "Summon a familiar.", IconGlyph = "☽", Unlocked = false, Progress = 0, UnlockedAt = "Locked" },
        new Achievement { AppId = 271590, GameName = "Grand Theft Auto V", Name = "Off the Road", Description = "Collect all spaceship parts.", IconGlyph = "★", Unlocked = true, Progress = 100, UnlockedAt = "Feb 19" }
    };

    public ObservableCollection<ContentProvider> Providers { get; } = new()
    {
        new ContentProvider { Name = "Steam Web API", BaseUrl = "api.steampowered.com", State = ProviderConnectionState.NotConfigured, Authentication = "API key not configured", RateLimit = "Unknown", LastRequest = "Never", LastError = "" },
        new ContentProvider { Name = "Ryuu Generator", BaseUrl = "generator.ryuu.lol", State = ProviderConnectionState.NotConfigured, Authentication = "Auth key not configured", RateLimit = "Unknown", LastRequest = "Never", LastError = "" },
        new ContentProvider { Name = "Local Library", BaseUrl = "Local filesystem", State = ProviderConnectionState.Healthy, Authentication = "Not required", RateLimit = "Unlimited", LastRequest = "Just now", LastError = "" }
    };

    public ObservableCollection<ModFix> ModFixes { get; } = new()
    {
        new ModFix { Id = "local-config-001", Name = "Display configuration backup", GameAppId = 730, Version = "1.2.0", Description = "Back up and validate the local display configuration.", Compatibility = "CS2 current", Status = "Ready", BackupRequired = true },
        new ModFix { Id = "local-input-002", Name = "Input profile validator", GameAppId = 1245620, Version = "0.9.4", Description = "Check local controller and input profile compatibility.", Compatibility = "Elden Ring current", Status = "Applied", BackupRequired = true },
        new ModFix { Id = "local-shader-003", Name = "Shader cache cleanup", GameAppId = 1172470, Version = "2.1.1", Description = "Safely identify stale local shader cache files.", Compatibility = "Apex Legends current", Status = "Available", BackupRequired = false }
    };

    public ObservableCollection<GenerationTemplate> GenerationTemplates { get; } = new()
    {
        new GenerationTemplate { Name = "Achievement UI preview", Type = "Test data", Description = "Generate local mock achievement metadata for UI and developer tests.", OutputFormat = "JSON", IsMockOnly = true, LastGenerated = "Never", Status = "Ready" },
        new GenerationTemplate { Name = "Manifest metadata preview", Type = "Metadata", Description = "Create a local metadata preview from user-provided, authorized manifest information.", OutputFormat = "JSON", IsMockOnly = true, LastGenerated = "Never", Status = "Ready" },
        new GenerationTemplate { Name = "Branch catalog export", Type = "Export", Description = "Prepare a local export of branch metadata already available to the application.", OutputFormat = "CSV / JSON", IsMockOnly = true, LastGenerated = "Never", Status = "Ready" }
    };

    public ObservableCollection<LogEntry> Logs { get; } = new()
    {
        new LogEntry { Timestamp = DateTime.Now.AddMinutes(-2), Level = LogLevel.Info, Component = "DownloadManager", Message = "Download completed and verification passed.", AppId = 730 },
        new LogEntry { Timestamp = DateTime.Now.AddMinutes(-9), Level = LogLevel.Warning, Component = "SteamApiHealth", Message = "Steam API credentials are not configured; using local demo data." },
        new LogEntry { Timestamp = DateTime.Now.AddMinutes(-18), Level = LogLevel.Info, Component = "LibraryService", Message = "Local library scan completed: 6 games found." },
        new LogEntry { Timestamp = DateTime.Now.AddHours(-1), Level = LogLevel.Debug, Component = "ProviderHealth", Message = "Secret values are redacted from diagnostics." },
        new LogEntry { Timestamp = DateTime.Now.AddHours(-3), Level = LogLevel.Error, Component = "DepotDownloader", Message = "Executable path has not been configured." }
    };
}

/// <summary>
/// Production data store. It only contains what was actually read from this machine, so an
/// empty library or queue shows an empty state instead of invented demo content.
/// </summary>
public sealed class AppDataStore : IAppDataStore
{
    public ObservableCollection<Game> Games { get; } = new();
    public ObservableCollection<DownloadJob> Downloads { get; } = new();
    public ObservableCollection<Depot> Depots { get; } = new();
    public ObservableCollection<Manifest> Manifests { get; } = new();
    public ObservableCollection<Branch> Branches { get; } = new();
    public ObservableCollection<Achievement> Achievements { get; } = new();

    public ObservableCollection<ContentProvider> Providers { get; } = new()
    {
        new ContentProvider { Name = "Steam Web API", BaseUrl = "api.steampowered.com", State = ProviderConnectionState.NotConfigured, Authentication = "API key not configured", RateLimit = "Unknown" },
        new ContentProvider { Name = "Local Steam library", BaseUrl = "Local filesystem", State = ProviderConnectionState.Healthy, Authentication = "Not required", RateLimit = "Unlimited" }
    };

    public ObservableCollection<ModFix> ModFixes { get; } = new();
    public ObservableCollection<GenerationTemplate> GenerationTemplates { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
}

public interface ISettingsService
{
    AppSettings Load();
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(_path),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (settings is not null) return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }
}

public interface ISecureCredentialService
{
    Task SaveAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class DpapiCredentialService : ISecureCredentialService
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager", "credentials");

    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(Path.Combine(_directory, SafeFileName(key)), bytes, cancellationToken);
    }

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, SafeFileName(key));
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var clear = System.Security.Cryptography.ProtectedData.Unprotect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(clear);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, SafeFileName(key));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static string SafeFileName(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

public interface ILoggingService
{
    void Add(LogLevel level, string component, string message, int? appId = null, Guid? jobId = null);
}

/// <summary>
/// Decides where a log entry is inserted. Logs live in an ObservableCollection that the UI
/// binds to, so background callers (tool runners, downloads) must not touch it directly.
/// </summary>
public interface ILogDispatcher
{
    /// <summary>True when the caller already runs on the thread that owns the collection.</summary>
    bool IsCurrent { get; }

    /// <summary>Runs the insertion on the owning thread.</summary>
    void Post(Action action);
}

/// <summary>
/// Inserts directly on the calling thread. This is the default so that non-UI hosts (tests,
/// tools) stay deterministic and never silently drop a log entry where nothing pumps a queue.
/// </summary>
public sealed class InlineLogDispatcher : ILogDispatcher
{
    public static InlineLogDispatcher Instance { get; } = new();

    public bool IsCurrent => true;

    public void Post(Action action) => action();
}

/// <summary>
/// Marshals log insertions onto the WPF dispatcher. Registered by the application container;
/// falls back to an inline insert when no Application exists (e.g. during shutdown).
/// </summary>
public sealed class WpfLogDispatcher : ILogDispatcher
{
    private static System.Windows.Threading.Dispatcher? Current => System.Windows.Application.Current?.Dispatcher;

    public bool IsCurrent
    {
        get
        {
            var dispatcher = Current;
            return dispatcher is null || dispatcher.CheckAccess();
        }
    }

    public void Post(Action action)
    {
        var dispatcher = Current;
        if (dispatcher is null)
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}

public sealed class InMemoryLoggingService : ILoggingService
{
    private readonly IAppDataStore _store;
    private readonly ILocalDatabase _database;
    private readonly ILogDispatcher _dispatcher;

    public InMemoryLoggingService(IAppDataStore store, ILocalDatabase database, ILogDispatcher? dispatcher = null)
    {
        _store = store;
        _database = database;
        _dispatcher = dispatcher ?? InlineLogDispatcher.Instance;
    }

    public void Add(LogLevel level, string component, string message, int? appId = null, Guid? jobId = null)
    {
        var entry = new LogEntry { Timestamp = DateTime.Now, Level = level, Component = component, Message = Redact(message), AppId = appId, JobId = jobId };

        if (_dispatcher.IsCurrent)
            Insert(entry);
        else
            _dispatcher.Post(() => Insert(entry));
    }

    private void Insert(LogEntry entry)
    {
        _store.Logs.Insert(0, entry);
        _ = _database.AppendLogAsync(entry);
    }
    private static string Redact(string value)
    {
        var redacted = Regex.Replace(value, @"(?i)(x-auth-key|auth_key|api_key)\s*[:=]\s*[^\s,;]+", "$1=[redacted]");
        return Regex.Replace(redacted, @"(?i)(authorization)\s*:\s*[^\s,;]+", "$1: [redacted]");
    }
}

public interface IDownloadManager
{
    Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default);

    /// <summary>Removes the job from the stored queue. Local files are never touched.</summary>
    Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default);
}

public sealed class DemoDownloadManager : IDownloadManager
{
    private readonly ILoggingService _logging;
    public DemoDownloadManager(ILoggingService logging) => _logging = logging;

    public async Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        job.State = DownloadJobState.Downloading;
        job.Status = "Downloading content";
        for (var progress = Math.Max(1, (int)job.Progress); progress <= 100; progress += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Progress = progress;
            await Task.Delay(80, cancellationToken);
        }
        job.State = DownloadJobState.Completed;
        job.Status = "Verified successfully";
        job.Progress = 100;
        _logging.Add(LogLevel.Info, "DownloadManager", "Demo download completed.", job.AppId, job.Id);
        return true;
    }

    public Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        job.State = DownloadJobState.Paused;
        job.Status = "Paused by user";
        return Task.CompletedTask;
    }

    public Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        job.State = DownloadJobState.Cancelled;
        job.Status = "Cancelled by user";
        return Task.CompletedTask;
    }

    public Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        job.State = DownloadJobState.Queued;
        job.Status = "Queued for retry";
        job.Progress = 0;
        return Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        job.Status = "Demo verification passed";
        job.State = DownloadJobState.Completed;
        return Task.FromResult(true);
    }

    public Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface ISteamApiHealthService { Task<ProviderConnectionState> CheckAsync(CancellationToken cancellationToken = default); }
public sealed class DemoSteamApiHealthService : ISteamApiHealthService { public Task<ProviderConnectionState> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(ProviderConnectionState.NotConfigured); }
public interface IDepotDownloaderService
{
    Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<DepotDownloaderRunResult> DownloadAsync(DepotDownloaderRequest request, IProgress<DepotDownloaderProgress>? progress = null, CancellationToken cancellationToken = default);
    Task StopAsync(Guid jobId, bool pause, CancellationToken cancellationToken = default);
}

public sealed class DemoDepotDownloaderService : IDepotDownloaderService
{
    public Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default)
        => Task.FromResult(new DepotDownloaderToolStatus(false, executablePath, string.Empty, "Demo fallback", DateTime.Now));

    public async Task<DepotDownloaderRunResult> DownloadAsync(DepotDownloaderRequest request, IProgress<DepotDownloaderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        for (var percent = 0; percent <= 100; percent += 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DepotDownloaderProgress(percent, "demo-content.bin", $"{percent} MB", "100 MB", "10 MB/s", $"{Math.Max(0, 10 - percent / 10)} sec", $"{percent}% demo progress"));
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        return new DepotDownloaderRunResult(0, false, false, "Demo DepotDownloader output", string.Empty);
    }

    public Task StopAsync(Guid jobId, bool pause, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public interface IManifestService { Task<bool> ValidateAsync(Manifest manifest, CancellationToken cancellationToken = default); }
public sealed class DemoManifestService : IManifestService { public Task<bool> ValidateAsync(Manifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(manifest.ValidationStatus == "Valid"); }
public sealed class DemoFileVerificationService : IFileVerificationService
{
    public Task<FileVerificationResult> VerifyAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new FileVerificationResult(true, 1, 1024, "Demo verification passed"));
}
public interface IProviderHealthService { Task<ProviderConnectionState> CheckAsync(ContentProvider provider, CancellationToken cancellationToken = default); }
public sealed class DemoProviderHealthService : IProviderHealthService { public Task<ProviderConnectionState> CheckAsync(ContentProvider provider, CancellationToken cancellationToken = default) => Task.FromResult(provider.State); }
public interface IRyuuGeneratorService { Task<string> FetchAsync(int appId, string? branch = null, CancellationToken cancellationToken = default); }
public sealed class DemoRyuuGeneratorService : IRyuuGeneratorService { public Task<string> FetchAsync(int appId, string? branch = null, CancellationToken cancellationToken = default) => Task.FromResult($"{{\"status\":\"not-configured\",\"appid\":{appId},\"branch\":\"{branch ?? "public"}\"}}"); }
public interface IDiskSpaceService { Task<string> GetAvailableAsync(string path, CancellationToken cancellationToken = default); }
public sealed class DemoDiskSpaceService : IDiskSpaceService { public Task<string> GetAvailableAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult("684 GB available"); }
public interface INotificationService { void Show(string title, string message); }
public sealed class DemoNotificationService : INotificationService { public void Show(string title, string message) { } }

/// <summary>Notification sink that records what happened instead of silently dropping it.</summary>
public sealed class LoggingNotificationService : INotificationService
{
    private readonly ILoggingService _logging;

    public LoggingNotificationService(ILoggingService logging) => _logging = logging;

    public void Show(string title, string message) =>
        _logging.Add(LogLevel.Info, "Notification", $"{title}: {message}");
}
public interface IUpdateService { Task<bool> CheckAsync(CancellationToken cancellationToken = default); }
public sealed class DemoUpdateService : IUpdateService { public Task<bool> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(false); }
public interface ILibraryService { }
public interface IGameMetadataService { }
public interface ISteamAuthService { }
public interface ISteamApiClient { }
public interface ISteamAppService { }
public interface ISteamUserService { }
public interface ISteamAchievementService { }
public interface IHubcapProvider { }
public interface IContentProvider { }
public interface IModFixService { }

public sealed class DownloadOptions
{
    public string TargetFolder { get; init; } = string.Empty;
    public int MaxParallelJobs { get; init; } = 2;
    public bool VerifyAfterDownload { get; init; } = true;
}

public sealed class ManifestSelection
{
    public int DepotId { get; init; }
    public string ManifestId { get; init; } = string.Empty;
}

public sealed class DepotSelection
{
    public int DepotId { get; init; }
    public bool Selected { get; init; }
}
