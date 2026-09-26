using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class GoldbergViewModel : ViewModelBase
{
    private const string ReleasesApi = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";
    private const string WebApiKeyName = "steam-web-api-key";
    private const string ManifestName = ".resonance_gbe.json";
    private const string BackupSuffix = ".gbe_bak";

    private static readonly string EmuRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "gbe_fork");
    private static readonly string EmuDir = Path.Combine(EmuRoot, "current");
    private static readonly string VersionFile = Path.Combine(EmuRoot, "version.txt");

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly HttpClient _http;

    private string _emuVersion = string.Empty;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string _actionStatus = string.Empty;
    private bool _defenderBlocked;
    private string _gameFolder = string.Empty;
    private string _appId = string.Empty;
    private string _gameName = string.Empty;
    private string _exePath = string.Empty;
    private bool _useColdClient;
    private string _username = "Player";
    private string _language = "english";
    private bool _overlayEnabled = true;
    private bool _achievementsEnabled = true;
    private bool _lanOnly;
    private string _webApiKey = string.Empty;
    private bool _isApplied;
    private GbeOptions _opt = new();

    private static readonly string OptionsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "goldberg_options.json");

    public sealed class GbeOptions
    {
        public string SteamId { get; set; } = "";
        public string Country { get; set; } = "US";
        public bool UnlockAllDlc { get; set; } = true;
        public bool Offline { get; set; }
        public bool DisableNetworking { get; set; }
        public bool PortableSaves { get; set; }
        public bool RecordPlaytime { get; set; } = true;
        public string LaunchArgs { get; set; } = "";
        public string OverlayHotkey { get; set; } = "shift + tab";
        public string NotificationPosition { get; set; } = "bot_right";
        public bool ShowFps { get; set; }
        public bool AchievementPopups { get; set; } = true;
        public bool Screenshots { get; set; } = true;
        public string Username { get; set; } = "";
        public string Language { get; set; } = "english";
        public bool Overlay { get; set; } = true;
        public bool Achievements { get; set; } = true;
        public bool LanOnly { get; set; }
    }

    public GoldbergViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ISettingsService settings,
        ISecureCredentialService credentials)
        : base(store, navigation, logging)
    {
        _settings = settings;
        _credentials = credentials;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        InstallEmuCommand = new AsyncRelayCommand(() => InstallEmuAsync(force: true), () => !IsBusy);
        BrowseGameCommand = new RelayCommand(BrowseGame);
        BrowseExeCommand = new RelayCommand(BrowseExe, () => HasGameFolder);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && IsEmuInstalled && HasGameFolder);
        RestoreCommand = new RelayCommand(Restore, () => !IsBusy && IsApplied);
        LaunchCommand = new RelayCommand(Launch, () => IsApplied && UseColdClient);
        OpenEmuFolderCommand = new RelayCommand(() => OpenFolder(EmuRoot));
        OpenGameFolderCommand = new RelayCommand(() => OpenFolder(GameFolder), () => HasGameFolder);

        var s = settings.Load();
        if (!string.IsNullOrWhiteSpace(s.SteamUsername)) _username = s.SteamUsername;
        LoadOptions();

        if (File.Exists(VersionFile)) _emuVersion = File.ReadAllText(VersionFile).Trim();
        ActionStatus = IsEmuInstalled ? $"Goldberg (gbe_fork) {_emuVersion} ready." : "Goldberg not installed yet — downloading…";
        _ = InitAsync();
    }

    public static IReadOnlyList<string> Languages { get; } = new[]
    {
        "english", "german", "french", "spanish", "latam", "italian", "portuguese", "brazilian", "russian",
        "polish", "turkish", "japanese", "koreana", "schinese", "tchinese", "dutch", "swedish", "czech", "ukrainian"
    };


    public string EmuVersion { get => _emuVersion; private set { if (SetProperty(ref _emuVersion, value)) OnPropertyChanged(nameof(IsEmuInstalled)); } }
    public bool IsEmuInstalled => !string.IsNullOrWhiteSpace(EmuVersion) && Directory.Exists(EmuDir);
    public bool DefenderBlocked { get => _defenderBlocked; private set => SetProperty(ref _defenderBlocked, value); }
    public string EmuFolder => EmuRoot;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    public string ActionStatus { get => _actionStatus; private set => SetProperty(ref _actionStatus, value); }

    public string GameFolder
    {
        get => _gameFolder;
        set
        {
            if (!SetProperty(ref _gameFolder, value)) return;
            OnPropertyChanged(nameof(HasGameFolder));
            DetectGame();
            RefreshCommands();
        }
    }
    public bool HasGameFolder => !string.IsNullOrWhiteSpace(GameFolder) && Directory.Exists(GameFolder);

    public string AppId { get => _appId; set => SetProperty(ref _appId, value?.Trim() ?? string.Empty); }
    public string GameName { get => _gameName; private set => SetProperty(ref _gameName, value); }
    public string ExePath { get => _exePath; set => SetProperty(ref _exePath, value); }
    public bool UseColdClient
    {
        get => _useColdClient;
        set
        {
            if (!SetProperty(ref _useColdClient, value)) return;
            OnPropertyChanged(nameof(UseNormalMode));
            OnPropertyChanged(nameof(ColdClientSelected));
            RefreshCommands();
        }
    }

    // Radio buttons in one group also push "false" when the other one gets checked; only react to "true"
    public bool UseNormalMode { get => !_useColdClient; set { if (value) UseColdClient = false; } }
    public bool ColdClientSelected { get => _useColdClient; set { if (value) UseColdClient = true; } }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Language { get => _language; set => SetProperty(ref _language, value); }
    public bool OverlayEnabled { get => _overlayEnabled; set => SetProperty(ref _overlayEnabled, value); }
    public bool AchievementsEnabled { get => _achievementsEnabled; set => SetProperty(ref _achievementsEnabled, value); }
    public bool LanOnly { get => _lanOnly; set => SetProperty(ref _lanOnly, value); }
    public string WebApiKey { get => _webApiKey; set => SetProperty(ref _webApiKey, value?.Trim() ?? string.Empty); }
    public bool IsApplied { get => _isApplied; private set { if (SetProperty(ref _isApplied, value)) RefreshCommands(); } }

    public static IReadOnlyList<string> NotificationPositions { get; } =
        new[] { "bot_right", "bot_center", "bot_left", "top_right", "top_center", "top_left" };

    public string SteamId { get => _opt.SteamId; set { _opt.SteamId = value?.Trim() ?? ""; OnPropertyChanged(); } }
    public string Country { get => _opt.Country; set { _opt.Country = (value ?? "").Trim().ToUpperInvariant(); OnPropertyChanged(); } }
    public bool UnlockAllDlc { get => _opt.UnlockAllDlc; set { _opt.UnlockAllDlc = value; OnPropertyChanged(); } }
    public bool Offline { get => _opt.Offline; set { _opt.Offline = value; OnPropertyChanged(); } }
    public bool DisableNetworking { get => _opt.DisableNetworking; set { _opt.DisableNetworking = value; OnPropertyChanged(); } }
    public bool PortableSaves { get => _opt.PortableSaves; set { _opt.PortableSaves = value; OnPropertyChanged(); } }
    public bool RecordPlaytime { get => _opt.RecordPlaytime; set { _opt.RecordPlaytime = value; OnPropertyChanged(); } }
    public string LaunchArgs { get => _opt.LaunchArgs; set { _opt.LaunchArgs = value ?? ""; OnPropertyChanged(); } }
    public string OverlayHotkey { get => _opt.OverlayHotkey; set { _opt.OverlayHotkey = value ?? ""; OnPropertyChanged(); } }
    public string NotificationPosition { get => _opt.NotificationPosition; set { _opt.NotificationPosition = value ?? "bot_right"; OnPropertyChanged(); } }
    public bool ShowFps { get => _opt.ShowFps; set { _opt.ShowFps = value; OnPropertyChanged(); } }
    public bool AchievementPopups { get => _opt.AchievementPopups; set { _opt.AchievementPopups = value; OnPropertyChanged(); } }
    public bool Screenshots { get => _opt.Screenshots; set { _opt.Screenshots = value; OnPropertyChanged(); } }

    private void LoadOptions()
    {
        try
        {
            if (!File.Exists(OptionsFile)) return;
            _opt = JsonSerializer.Deserialize<GbeOptions>(File.ReadAllText(OptionsFile)) ?? new GbeOptions();
            if (!string.IsNullOrWhiteSpace(_opt.Username)) _username = _opt.Username;
            if (!string.IsNullOrWhiteSpace(_opt.Language)) _language = _opt.Language;
            _overlayEnabled = _opt.Overlay;
            _achievementsEnabled = _opt.Achievements;
            _lanOnly = _opt.LanOnly;
        }
        catch { _opt = new GbeOptions(); }
    }

    private void SaveOptions()
    {
        try
        {
            _opt.Username = Username; _opt.Language = Language; _opt.Overlay = OverlayEnabled;
            _opt.Achievements = AchievementsEnabled; _opt.LanOnly = LanOnly;
            Directory.CreateDirectory(Path.GetDirectoryName(OptionsFile)!);
            File.WriteAllText(OptionsFile, JsonSerializer.Serialize(_opt, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public IAsyncRelayCommand InstallEmuCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public ICommand OpenEmuFolderCommand { get; }
    public RelayCommand OpenGameFolderCommand { get; }


    private void RefreshCommands()
    {
        InstallEmuCommand?.NotifyCanExecuteChanged();
        BrowseExeCommand?.NotifyCanExecuteChanged();
        ApplyCommand?.NotifyCanExecuteChanged();
        RestoreCommand?.NotifyCanExecuteChanged();
        LaunchCommand?.NotifyCanExecuteChanged();
        OpenGameFolderCommand?.NotifyCanExecuteChanged();
    }

    private async Task InitAsync()
    {
        var key = await _credentials.ReadAsync(WebApiKeyName);
        if (!string.IsNullOrWhiteSpace(key)) WebApiKey = key;
        await InstallEmuAsync(force: false);
    }

    // ── Emulator download (Detanup01/gbe_fork) ──────────────────────

    private async Task InstallEmuAsync(bool force)
    {
        IsBusy = true;
        DefenderBlocked = false;
        try
        {
            BusyText = "Checking GitHub…";
            using var resp = await _http.GetAsync(ReleasesApi);
            if (!resp.IsSuccessStatusCode)
            {
                ActionStatus = $"GitHub: HTTP {(int)resp.StatusCode}" + (IsEmuInstalled ? " — keeping installed version." : "");
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
            if (!force && IsEmuInstalled && tag == EmuVersion)
            {
                ActionStatus = $"Goldberg {tag} is up to date.";
                return;
            }

            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => a.Name.StartsWith("emu-win-release", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(asset.Url)) { ActionStatus = "No Windows release asset found on GitHub."; return; }

            Directory.CreateDirectory(EmuRoot);
            var archive = Path.Combine(EmuRoot, asset.Name);
            ActionStatus = $"Downloading {asset.Name} ({tag})…";
            await DownloadWithProgressAsync(asset.Url, archive);

            BusyText = "Extracting…";
            var tmp = Path.Combine(EmuRoot, "extract_tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            await Task.Run(() =>
            {
                // Solid 7z: per-entry extraction re-decompresses from the start each time, so stream it once
                using var arc = ArchiveFactory.Open(archive);
                using var reader = arc.ExtractAllEntries();
                var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory) reader.WriteEntryToDirectory(tmp, options);
            });

            if (Directory.Exists(EmuDir)) Directory.Delete(EmuDir, true);
            Directory.Move(tmp, EmuDir);
            try { File.Delete(archive); } catch { }
            File.WriteAllText(VersionFile, tag);
            EmuVersion = tag;
            RefreshCommands();
            ActionStatus = $"Goldberg {tag} installed.";
            Logging.Add(LogLevel.Info, "Goldberg", $"Installed gbe_fork {tag}");
        }
        catch (Exception ex) when (IsVirusBlock(ex))
        {
            DefenderBlocked = true;
            ActionStatus = $"Windows Defender blocked the emulator files. Add an exclusion for {EmuRoot} in Windows Security yourself, then press Retry.";
        }
        catch (Exception ex)
        {
            ActionStatus = $"Goldberg download failed: {ex.Message}";
        }
        finally
        {
            BusyText = string.Empty;
            IsBusy = false;
        }
    }

    private async Task DownloadWithProgressAsync(string url, string target)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(target);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        var lastPct = -1;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total <= 0) continue;
            var pct = (int)(read * 100 / total);
            if (pct == lastPct) continue;
            lastPct = pct;
            BusyText = $"Downloading… {pct}%  ({read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB)";
        }
    }

    // ERROR_VIRUS_INFECTED (225) / ERROR_VIRUS_DELETED (226)
    private static bool IsVirusBlock(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var code = e.HResult & 0xFFFF;
            if (code is 225 or 226 || e.Message.Contains("virus", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Virus", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private string? FindEmuFile(string fileName, string folderSegment)
    {
        if (!Directory.Exists(EmuDir)) return null;
        return Directory.EnumerateFiles(EmuDir, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Split(Path.DirectorySeparatorChar).Contains(folderSegment, StringComparer.OrdinalIgnoreCase));
    }

    // ── Game selection ──────────────────────────────────────────────

    private void BrowseGame()
    {
        var dlg = new OpenFolderDialog { Title = "Select the game folder", Multiselect = false };
        if (dlg.ShowDialog() == true) GameFolder = dlg.FolderName;
    }

    private void BrowseExe()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select the game executable",
            Filter = "Executables (*.exe)|*.exe",
            InitialDirectory = HasGameFolder ? GameFolder : null
        };
        if (dlg.ShowDialog() == true) ExePath = dlg.FileName;
    }

    private void DetectGame()
    {
        GameName = string.Empty;
        ExePath = string.Empty;
        IsApplied = false;
        if (!HasGameFolder) return;

        var folder = GameFolder.TrimEnd('\\');
        var common = Path.GetDirectoryName(folder);
        var steamapps = common != null ? Path.GetDirectoryName(common) : null;
        if (common != null && steamapps != null &&
            Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            var installDir = Path.GetFileName(folder);
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var dir = ReadAcfValue(text, "installdir");
                if (!installDir.Equals(dir, StringComparison.OrdinalIgnoreCase)) continue;
                AppId = ReadAcfValue(text, "appid") ?? AppId;
                GameName = ReadAcfValue(text, "name") ?? string.Empty;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(AppId) || !int.TryParse(AppId, out _))
        {
            var appIdFile = Directory.EnumerateFiles(folder, "steam_appid.txt", SearchOption.AllDirectories).FirstOrDefault();
            if (appIdFile != null && int.TryParse(File.ReadAllText(appIdFile).Trim(), out var id)) AppId = id.ToString();
        }

        if (string.IsNullOrWhiteSpace(GameName)) GameName = Path.GetFileName(folder);
        ExePath = GuessMainExe(folder) ?? string.Empty;
        IsApplied = File.Exists(Path.Combine(folder, ManifestName));

        var apis = Directory.EnumerateFiles(folder, "steam_api*.dll", SearchOption.AllDirectories)
            .Count(IsSteamApiDll);
        ActionStatus = $"{GameName}: AppID {(string.IsNullOrWhiteSpace(AppId) ? "?" : AppId)}, {apis} steam_api dll(s)"
                       + (IsApplied ? ", Goldberg already applied." : ".");
        if (apis == 0 && !UseColdClient)
        {
            UseColdClient = true;
            ActionStatus = "No steam_api dll found — switched to ColdClient mode.";
        }
    }

    private static string? ReadAcfValue(string acf, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsSteamApiDll(string path)
    {
        var name = Path.GetFileName(path);
        return (name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
               && !path.Contains("steam_settings", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] ExeBlacklist =
        { "unins", "crash", "redist", "setup", "vcredist", "vc_redist", "dxsetup", "dotnet", "steamclient_loader", "UnityCrashHandler", "CrashReport", "EasyAntiCheat", "BEService" };

    private static string? GuessMainExe(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                .Where(p => Path.GetRelativePath(folder, p).Count(c => c == '\\') <= 3)
                .Where(p => !ExeBlacklist.Any(b => Path.GetFileName(p).Contains(b, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => new FileInfo(p).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ── Apply ───────────────────────────────────────────────────────

    private sealed class GbeManifest
    {
        public string Mode { get; set; } = "normal";
        public List<string> Created { get; set; } = new();
        public List<string> Backups { get; set; } = new();
        public string? LoaderExe { get; set; }
    }

    private async Task ApplyAsync()
    {
        if (!int.TryParse(AppId, out var appId) || appId <= 0) { ActionStatus = "Enter a valid AppID."; return; }
        if (UseColdClient && !File.Exists(ExePath)) { ActionStatus = "ColdClient needs the game exe — pick it first."; return; }

        IsBusy = true;
        try
        {
            SaveOptions();
            if (File.Exists(Path.Combine(GameFolder, ManifestName))) RestoreCore();

            if (!string.IsNullOrWhiteSpace(WebApiKey)) await _credentials.SaveAsync(WebApiKeyName, WebApiKey);

            JsonArray? achievements = null;
            List<(string Url, string File)> icons = new();
            if (AchievementsEnabled)
            {
                if (string.IsNullOrWhiteSpace(WebApiKey))
                    ActionStatus = "Achievements: no Steam Web API key set — skipping achievement list.";
                else
                {
                    BusyText = "Fetching achievements…";
                    (achievements, icons) = await FetchAchievementsAsync(appId);
                    ActionStatus = achievements == null ? "Achievements: could not fetch schema." : $"Achievements: {achievements.Count} found.";
                }
            }

            var manifest = new GbeManifest { Mode = UseColdClient ? "coldclient" : "normal" };
            var settingsDirs = new List<string>();

            BusyText = "Applying Goldberg…";
            await Task.Run(() =>
            {
                if (UseColdClient) ApplyColdClient(appId, manifest, settingsDirs);
                else ApplyNormal(manifest, settingsDirs);
            });

            foreach (var dir in settingsDirs)
                await WriteSteamSettingsAsync(dir, appId, achievements, icons, manifest);

            await File.WriteAllTextAsync(Path.Combine(GameFolder, ManifestName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            IsApplied = true;
            ActionStatus = UseColdClient
                ? $"Goldberg applied (ColdClient). Start the game with Launch or {Path.GetFileName(manifest.LoaderExe)}."
                : $"Goldberg applied to {manifest.Backups.Count} steam_api dll(s). Start the game normally.";
            Logging.Add(LogLevel.Info, "Goldberg", $"Applied to {GameName} ({appId}), mode {manifest.Mode}");
        }
        catch (Exception ex) when (IsVirusBlock(ex))
        {
            DefenderBlocked = true;
            ActionStatus = "Windows Defender blocked copying the emulator into the game folder. Add an exclusion yourself, then Apply again.";
        }
        catch (Exception ex)
        {
            ActionStatus = $"Apply failed: {ex.Message}";
        }
        finally
        {
            BusyText = string.Empty;
            IsBusy = false;
        }
    }

    private void ApplyNormal(GbeManifest manifest, List<string> settingsDirs)
    {
        var variant = OverlayEnabled ? "experimental" : "regular";
        var dlls = Directory.EnumerateFiles(GameFolder, "steam_api*.dll", SearchOption.AllDirectories).Where(IsSteamApiDll).ToList();
        if (dlls.Count == 0) throw new InvalidOperationException("No steam_api(64).dll in the game folder — use ColdClient mode.");

        foreach (var dll in dlls)
        {
            var emu = FindEmuFile(Path.GetFileName(dll), variant)
                      ?? throw new FileNotFoundException($"{Path.GetFileName(dll)} missing in the {variant} emu build.");
            var bak = dll + BackupSuffix;
            if (!File.Exists(bak)) File.Copy(dll, bak);
            manifest.Backups.Add(dll);

            var dir = Path.GetDirectoryName(dll)!;
            var interfaces = GenerateInterfaces(bak);
            File.Copy(emu, dll, overwrite: true);

            var ss = Path.Combine(dir, "steam_settings");
            if (!Directory.Exists(ss)) { Directory.CreateDirectory(ss); manifest.Created.Add(ss); }
            if (interfaces != null) File.WriteAllText(Path.Combine(ss, "steam_interfaces.txt"), interfaces);
            settingsDirs.Add(ss);
        }
    }

    private string? GenerateInterfaces(string originalDll)
    {
        var tool = Directory.Exists(EmuDir)
            ? Directory.EnumerateFiles(EmuDir, "generate_interfaces*.exe", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains("64")).FirstOrDefault()
            : null;
        if (tool == null) return null;

        var work = Path.Combine(Path.GetTempPath(), "rt_gbe_interfaces_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var psi = new ProcessStartInfo(tool) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(originalDll);
            using var p = Process.Start(psi);
            if (p == null || !p.WaitForExit(20000)) { try { p?.Kill(); } catch { } return null; }
            var file = Path.Combine(work, "steam_interfaces.txt");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch { return null; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private void ApplyColdClient(int appId, GbeManifest manifest, List<string> settingsDirs)
    {
        var is64 = IsExe64Bit(ExePath);
        var loaders = Directory.Exists(EmuDir)
            ? Directory.EnumerateFiles(EmuDir, "steamclient_loader_*.exe", SearchOption.AllDirectories)
                .Where(p => p.Contains("steamclient_experimental", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        var loader = loaders.FirstOrDefault(p => Path.GetFileName(p).Contains("64") == is64)
                     ?? throw new FileNotFoundException("steamclient_loader not found in the emu build.");

        var files = new List<string> { loader };
        foreach (var name in new[] { "steamclient.dll", "steamclient64.dll", "GameOverlayRenderer.dll", "GameOverlayRenderer64.dll" })
        {
            var f = FindEmuFile(name, "steamclient_experimental");
            if (f != null) files.Add(f);
            else if (name.StartsWith("steamclient")) throw new FileNotFoundException($"{name} not found in the emu build.");
        }

        foreach (var src in files)
        {
            var dest = Path.Combine(GameFolder, Path.GetFileName(src));
            if (File.Exists(dest) && !File.Exists(dest + BackupSuffix))
            {
                File.Copy(dest, dest + BackupSuffix);
                manifest.Backups.Add(dest);
            }
            else if (!File.Exists(dest)) manifest.Created.Add(dest);
            File.Copy(src, dest, overwrite: true);
        }

        var loaderDest = Path.Combine(GameFolder, Path.GetFileName(loader));
        manifest.LoaderExe = loaderDest;

        var exeRel = Path.GetRelativePath(GameFolder, ExePath);
        var ini = Path.Combine(GameFolder, "ColdClientLoader.ini");
        if (File.Exists(ini) && !manifest.Backups.Contains(ini)) { File.Copy(ini, ini + BackupSuffix, true); manifest.Backups.Add(ini); }
        else if (!File.Exists(ini)) manifest.Created.Add(ini);
        File.WriteAllLines(ini, new[]
        {
            "[SteamClient]",
            $"Exe={exeRel}",
            "ExeRunDir=",
            $"ExeCommandLine={LaunchArgs.Replace("\r", "").Replace("\n", " ").Trim()}",
            $"AppId={appId}",
            "SteamClientDll=steamclient.dll",
            "SteamClient64Dll=steamclient64.dll",
            "",
            "[Injection]",
            "ForceInjectSteamClient=0",
            "ForceInjectGameOverlayRenderer=0",
            "DllsToInjectFolder=",
            "IgnoreInjectionError=1",
            "IgnoreLoaderArchDifference=0",
            "",
            "[Persistence]",
            "Mode=0",
            "",
            "[Debug]",
            "ResumeByDebugger=0"
        });

        var ss = Path.Combine(GameFolder, "steam_settings");
        if (!Directory.Exists(ss)) { Directory.CreateDirectory(ss); manifest.Created.Add(ss); }
        settingsDirs.Add(ss);
    }

    private static bool IsExe64Bit(string exe)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            fs.Seek(br.ReadInt32() + 4, SeekOrigin.Begin);
            return br.ReadUInt16() == 0x8664;
        }
        catch { return true; }
    }

    private async Task WriteSteamSettingsAsync(string ss, int appId, JsonArray? achievements,
        List<(string Url, string File)> icons, GbeManifest manifest)
    {
        void Write(string name, string content)
        {
            var path = Path.Combine(ss, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        static string Clean(string v) => v.Replace("\r", "").Replace("\n", "").Trim();
        static int B(bool v) => v ? 1 : 0;

        Write("steam_appid.txt", appId.ToString());

        var user = new StringBuilder("[user::general]\r\n");
        user.Append($"account_name={Clean(Username)}\r\nlanguage={Language}\r\n");
        if (System.Text.RegularExpressions.Regex.IsMatch(SteamId, @"^7656119\d{10}$")) user.Append($"account_steamid={SteamId}\r\n");
        if (System.Text.RegularExpressions.Regex.IsMatch(Country, "^[A-Z]{2}$")) user.Append($"ip_country={Country}\r\n");
        if (PortableSaves) user.Append("\r\n[user::saves]\r\nlocal_save_path=./GSE Saves\r\n");
        Write("configs.user.ini", user.ToString());

        Write("configs.main.ini",
            "[main::stats]\r\n" +
            $"record_playtime={B(RecordPlaytime)}\r\n\r\n" +
            "[main::connectivity]\r\n" +
            $"disable_lan_only={B(!LanOnly)}\r\n" +
            $"disable_networking={B(DisableNetworking)}\r\n" +
            $"offline={B(Offline)}\r\n");

        Write("configs.app.ini", $"[app::dlcs]\r\nunlock_all={B(UnlockAllDlc)}\r\n");

        Write("configs.overlay.ini",
            "[overlay::general]\r\n" +
            $"enable_experimental_overlay={B(OverlayEnabled)}\r\n" +
            $"disable_achievement_notification={B(!AchievementPopups)}\r\n" +
            $"overlay_always_show_fps={B(ShowFps)}\r\n" +
            $"enable_screenshot={B(Screenshots)}\r\n\r\n" +
            "[overlay::hotkeys]\r\n" +
            $"key_combo={(string.IsNullOrWhiteSpace(OverlayHotkey) ? "shift + tab" : Clean(OverlayHotkey))}\r\n\r\n" +
            "[overlay::appearance]\r\n" +
            $"PosAchievement={NotificationPosition}\r\n");

        if (achievements != null)
        {
            Write("achievements.json", achievements.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var imgDir = Path.Combine(ss, "images");
            Directory.CreateDirectory(imgDir);
            BusyText = $"Downloading {icons.Count} achievement icons…";
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(icons.Select(async icon =>
            {
                await gate.WaitAsync();
                try
                {
                    var bytes = await _http.GetByteArrayAsync(icon.Url);
                    await File.WriteAllBytesAsync(Path.Combine(ss, icon.File), bytes);
                }
                catch { }
                finally { gate.Release(); }
            }));
        }
    }

    private async Task<(JsonArray?, List<(string Url, string File)>)> FetchAchievementsAsync(int appId)
    {
        var icons = new List<(string, string)>();
        try
        {
            var lang = Language == "english" ? "en" : Language;
            var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={Uri.EscapeDataString(WebApiKey)}&appid={appId}&l={lang}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (null, icons);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("game", out var game) ||
                !game.TryGetProperty("availableGameStats", out var stats) ||
                !stats.TryGetProperty("achievements", out var list))
                return (new JsonArray(), icons);

            var result = new JsonArray();
            foreach (var a in list.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var icon = a.TryGetProperty("icon", out var i) ? i.GetString() : null;
                var gray = a.TryGetProperty("icongray", out var g) ? g.GetString() : null;
                var iconFile = $"images/{safe}.jpg";
                var grayFile = $"images/{safe}_gray.jpg";
                if (!string.IsNullOrEmpty(icon)) icons.Add((icon, iconFile));
                if (!string.IsNullOrEmpty(gray)) icons.Add((gray, grayFile));

                result.Add(new JsonObject
                {
                    ["name"] = name,
                    ["displayName"] = a.TryGetProperty("displayName", out var dn) ? dn.GetString() : name,
                    ["description"] = a.TryGetProperty("description", out var d) ? d.GetString() : "",
                    ["hidden"] = a.TryGetProperty("hidden", out var h) ? h.ToString() : "0",
                    ["icon"] = iconFile,
                    ["icongray"] = grayFile
                });
            }
            return (result, icons);
        }
        catch { return (null, icons); }
    }

    // ── Restore / launch ────────────────────────────────────────────

    private void Restore()
    {
        try
        {
            RestoreCore();
            IsApplied = false;
            ActionStatus = "Original files restored, Goldberg removed.";
        }
        catch (Exception ex) { ActionStatus = $"Restore failed: {ex.Message}"; }
    }

    private void RestoreCore()
    {
        var manifestPath = Path.Combine(GameFolder, ManifestName);
        if (!File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<GbeManifest>(File.ReadAllText(manifestPath)) ?? new GbeManifest();

        foreach (var original in manifest.Backups)
        {
            var bak = original + BackupSuffix;
            if (!File.Exists(bak)) continue;
            File.Copy(bak, original, overwrite: true);
            File.Delete(bak);
        }

        // Only paths inside the game folder that we recorded as newly created
        var root = Path.GetFullPath(GameFolder).TrimEnd('\\') + "\\";
        foreach (var path in manifest.Created.Where(p => Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }

        File.Delete(manifestPath);
    }

    private void Launch()
    {
        var manifestPath = Path.Combine(GameFolder, ManifestName);
        if (!File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<GbeManifest>(File.ReadAllText(manifestPath));
        if (manifest?.LoaderExe == null || !File.Exists(manifest.LoaderExe)) { ActionStatus = "Loader not found — Apply again."; return; }
        Process.Start(new ProcessStartInfo(manifest.LoaderExe) { WorkingDirectory = GameFolder, UseShellExecute = true });
        ActionStatus = $"Started {GameName} via ColdClientLoader.";
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
