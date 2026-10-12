using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;
using UCO2.Patcher.Core;
using GameScanResult = UCO2.Patcher.Core.GameScanResult;

namespace Steamy.ViewModels;

public sealed class UcOnlineViewModel : ObservableObject
{
    private readonly UcOnlineService _service;
    private readonly ISecureCredentialService? _credentials;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SteamSearchResult>>> _search;
    private readonly string _dataRoot;
    private CancellationTokenSource? _cancel;
    private GameScanResult? _scan;
    private string _folder = "", _executable = "", _appId = "", _networkId = "480", _status = "Choose a game folder to get started.", _review = "";
    private bool _busy;
    private PatchOptions _options = new() { QuarantineCompetingFiles = false, UnlockAllDlc = false, ForceOwnership = false };
    private BackupSnapshot? _backup;
    private SteamSearchResult? _match;
    public UcOnlineViewModel(UcOnlineService service, ISecureCredentialService? credentials = null,
        Func<string, CancellationToken, Task<IReadOnlyList<SteamSearchResult>>>? search = null, string? dataRoot = null)
    {
        _service = service;
        _credentials = credentials;
        _search = search ?? SearchSteamAsync;
        _dataRoot = dataRoot ?? Steamy.Storage.AppDataPaths.Root;
        InspectCommand = new AsyncRelayCommand(InspectAsync, () => CanEdit && !string.IsNullOrWhiteSpace(Folder));
        ReviewCommand = new AsyncRelayCommand(() => PlanAsync(false), () => CanEdit && _scan is not null);
        ApplyCommand = new AsyncRelayCommand(() => PlanAsync(true), () => CanEdit && _scan is not null);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanEdit && SelectedBackup is not null);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy);
        try
        {
            var path = Path.Combine(_dataRoot, "uc-online.preferences.json");
            if (File.Exists(path) && new FileInfo(path).Length < 8192)
            {
                var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
                if (saved is not null) { _folder = saved.Folder; _appId = saved.AppId; _networkId = saved.NetworkAppId; }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public string Folder { get => _folder; set { if (SetProperty(ref _folder, value)) { _scan = null; Executable = ""; AppId = ""; NetworkAppId = "480"; Review = ""; Backups.Clear(); Matches.Clear(); SelectedBackup = null; SelectedMatch = null; Options = new() { QuarantineCompetingFiles = false, UnlockAllDlc = false, ForceOwnership = false }; Status = "Choose Inspect game to check this folder."; NotifyCommands(); } } }
    public string Executable { get => _executable; set => SetProperty(ref _executable, value); }
    public string AppId { get => _appId; set => SetProperty(ref _appId, value); }
    public string NetworkAppId { get => _networkId; set => SetProperty(ref _networkId, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Review { get => _review; private set => SetProperty(ref _review, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } } }
    public bool CanEdit => !IsBusy;
    public PatchOptions Options { get => _options; private set => SetProperty(ref _options, value); }
    public ObservableCollection<BackupSnapshot> Backups { get; } = [];
    public ObservableCollection<SteamSearchResult> Matches { get; } = [];
    public BackupSnapshot? SelectedBackup { get => _backup; set { if (SetProperty(ref _backup, value)) NotifyCommands(); } }
    public SteamSearchResult? SelectedMatch { get => _match; set { if (SetProperty(ref _match, value) && value is not null) AppId = value.AppId.ToString(); } }
    public IAsyncRelayCommand InspectCommand { get; }
    public IAsyncRelayCommand ReviewCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IRelayCommand CancelCommand { get; }
    private void NotifyCommands() { InspectCommand.NotifyCanExecuteChanged(); ReviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cancel = new(); IsBusy = true;
        try { await action(_cancel.Token); }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception e) { Status = e.Message; }
        finally { IsBusy = false; _cancel.Dispose(); _cancel = null; }
    }
    private Task InspectAsync() => RunAsync(async token =>
    {
        Status = "Inspecting your game…";
        var scan = await _service.ScanAsync(Folder, token);
        _scan = scan; Executable = scan.ExecutablePath;
        Options = new() { InstallPhoton = (scan.Backends & (BackendKind.PhotonRealtime | BackendKind.PhotonFusion | BackendKind.PhotonVoice)) != 0,
            InstallEos = scan.Backends.HasFlag(BackendKind.Eos), InstallPlayFab = scan.Backends.HasFlag(BackendKind.PlayFab), InstallCoherence = scan.Backends.HasFlag(BackendKind.Coherence),
            EnableSteamStub = scan.SteamStub == SteamStubStatus.Detected, QuarantineCompetingFiles = false, UnlockAllDlc = false, ForceOwnership = false };
        if (_credentials is not null)
        {
            var saved = await _credentials.ReadAsync(SettingsKey(scan.GameDirectory), token);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                Options = JsonSerializer.Deserialize<PatchOptions>(saved) ?? Options;
                if (Options.OriginalAppId > 0) AppId = Options.OriginalAppId.ToString();
                NetworkAppId = Options.AppId.ToString();
            }
        }
        var iniPath = Path.Combine(scan.ConfigDirectory, "steam_appid.txt");
        if (File.Exists(iniPath) && new FileInfo(iniPath).Length < 32 && uint.TryParse(await File.ReadAllTextAsync(iniPath, token), out var id) && id != 480) AppId = id.ToString();
        await RefreshBackupsAsync(scan.GameDirectory, token);
        Status = scan.EngineLabel + " · " + scan.BackendLabel;
        Review = string.Join(Environment.NewLine, scan.Warnings);
        Matches.Clear();
        try
        {
            foreach (var match in await _search(scan.GameDirectory, token)) Matches.Add(match);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException) { }
    });
    private Task PlanAsync(bool apply) => RunAsync(async token =>
    {
        if (_scan is null) return;
        if (!uint.TryParse(AppId, out var appId) || appId == 0 || !uint.TryParse(NetworkAppId, out var network) || network == 0) throw new InvalidOperationException("Enter valid game and network App IDs.");
        var game = await Task.Run(() => _service.SelectExecutable(_scan, Executable), token);
        Options.OriginalAppId = appId; Options.AppId = network;
        var progress = new Progress<string>(value => { if (IsBusy && !token.IsCancellationRequested) Status = value; });
        var plan = await _service.PlanAsync(game, Options, progress, token);
        if (_credentials is not null) await _credentials.SaveAsync(SettingsKey(game.GameDirectory), JsonSerializer.Serialize(Options), token);
        Review = string.Join(Environment.NewLine, plan.Operations.Select(operation => operation.Description + " · " + Path.GetRelativePath(_scan.GameDirectory, operation.TargetPath)))
            + (plan.Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings));
        if (!apply) { Status = "Ready. Review the files below, then install UC Online."; return; }
        await _service.ApplyAsync(plan, progress, token);
        Directory.CreateDirectory(_dataRoot);
        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "uc-online.preferences.json"), JsonSerializer.Serialize(new Preferences(Folder, AppId, NetworkAppId)), CancellationToken.None);
        await RefreshBackupsAsync(_scan.GameDirectory, CancellationToken.None);
        Status = "UC Online installed. Steam and your game can be started when you are ready.";
    });
    private Task RestoreAsync() => RunAsync(async token =>
    {
        if (SelectedBackup is null) return;
        await _service.RestoreAsync(SelectedBackup.ManifestPath, Folder, new Progress<string>(value => Status = value), token);
        await RefreshBackupsAsync(Folder, token); Status = "Original files restored.";
    });
    private async Task RefreshBackupsAsync(string folder, CancellationToken token)
    {
        Backups.Clear(); foreach (var backup in await _service.BackupsAsync(folder, token)) if (backup.Status is "Complete" or "RollbackFailed" or "InProgress") Backups.Add(backup);
        SelectedBackup = Backups.FirstOrDefault();
    }
    private sealed record Preferences(string Folder, string AppId, string NetworkAppId);
    private static async Task<IReadOnlyList<SteamSearchResult>> SearchSteamAsync(string folder, CancellationToken token)
    { using var service = new SteamStoreSearchService(); return await service.SearchAsync(folder, token); }
    private static string SettingsKey(string folder) => "uc-online-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant())))[..24];
}
