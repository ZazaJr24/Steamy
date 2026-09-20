using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

/// <summary>
/// Integrierte Denuvo-Activation-Seite: das Tool wird direkt hier ausgeführt, ohne ein separates
/// Tool-Runner-Fenster. Der Nutzer wählt ein installiertes Spiel aus der lokalen Library aus
/// oder gibt eine AppID manuell ein, klickt auf „Ticket holen", und die App erstellt daraufhin
/// die config.user.ini auf dem Desktop.
/// </summary>
public sealed class DenuvoActivationViewModel : ViewModelBase
{
    private readonly IAppDataStore _store;
    private readonly ILocalToolRunner _runner;
    private readonly IDenuvoGeneratorDownloadService _downloader;
    private readonly ISettingsService _settings;
    private readonly ILoggingService _logging;
    private CancellationTokenSource? _runCts;

    private Game? _selectedGame;
    private string _manualAppId = string.Empty;
    private string _toolExecutablePath = string.Empty;
    private string _status = "Lade Tool-Informationen …";
    private string _ticketShort = string.Empty;
    private string _steamIdDisplay = string.Empty;
    private string _configInfo = string.Empty;
    private bool _isBusy;
    private bool _hasResult;
    private bool _isDownloadingTool;
    private string _downloadMessage = string.Empty;
    private string _toolOutput = string.Empty;

    public DenuvoActivationViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ILocalToolRunner runner,
        IDenuvoGeneratorDownloadService downloader,
        ISettingsService settings)
        : base(store, navigation, logging)
    {
        _store = store;
        _runner = runner;
        _downloader = downloader;
        _settings = settings;
        _logging = logging;

        InstallierteSpiele = new ObservableCollection<Game>(
            _store.Games.Where(g => g.InstallState == GameInstallState.Installed));

        // Das Tool wird NICHT im Konstruktor geladen: das passiert erst, wenn die Page es
        // explizit anfordert (siehe DenuvoActivationPage.Loaded), damit der Konstruktor nicht
        // während eines WebRequests hängt.
        if (!_downloader.HasCachedExecutable)
        {
            Status = "Tool noch nicht heruntergeladen.";
        }
        else
        {
            _toolExecutablePath = _downloader.CachedPath!;
            _downloadMessage = string.Empty;
            Status = "AppID eingeben und Ticket holen.";
        }


        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        ClearResultCommand = new RelayCommand(ClearResult);
        BrowseToolCommand = new RelayCommand(BrowseToolCommandExecuted);
    }

    /// <summary>Installierte Spiele aus der lokalen Library (Name + AppID).</summary>
    public ObservableCollection<Game> InstallierteSpiele { get; }

    /// <summary>Aktuell ausgewähltes Installations-Spiel.</summary>
    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                OnPropertyChanged(nameof(HasGameSelection));
                OnPropertyChanged(nameof(SelectedAppIdLabel));
                UpdateManualAppIdFromGame();
                NotifyCanRunChanged();
            }
        }
    }

    public bool HasGameSelection => _selectedGame is not null;

    public string SelectedAppIdLabel => _selectedGame is null
        ? "—"
        : $"App {_selectedGame.AppId} · {_selectedGame.Name}";

    /// <summary>Manuelle AppID-Eingabe (z. B. für Spiele, die nicht in der Library sind).</summary>
    public string ManualAppId
    {
        get => _manualAppId;
        set
        {
            if (SetProperty(ref _manualAppId, value))
            {
                OnPropertyChanged(nameof(HasManualAppId));
                UpdateGameSelectionFromManual();
                NotifyCanRunChanged();
            }
        }
    }

    public bool HasManualAppId => !string.IsNullOrWhiteSpace(_manualAppId);

    /// <summary>Pfad zum steam-ticket-generator.exe (wird aus dem Cache geladen).</summary>
    public string ToolExecutablePath
    {
        get => _toolExecutablePath;
        private set
        {
            if (SetProperty(ref _toolExecutablePath, value))
                OnPropertyChanged(nameof(ToolDisplayName));
        }
    }

    public string ToolDisplayName => string.IsNullOrWhiteSpace(_toolExecutablePath)
        ? "Steam-Ticket-Generator.exe nicht verfügbar"
        : Path.GetFileName(_toolExecutablePath);

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Kurzform des Tickets zur Anzeige (erster Teil + Länge).</summary>
    public string TicketShort
    {
        get => _ticketShort;
        private set => SetProperty(ref _ticketShort, value);
    }

    public string SteamIdDisplay
    {
        get => _steamIdDisplay;
        private set => SetProperty(ref _steamIdDisplay, value);
    }

    /// <summary>Hinweis, ob und wo config.user.ini erstellt wurde.</summary>
    public string ConfigInfo
    {
        get => _configInfo;
        private set => SetProperty(ref _configInfo, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanRun));
                NotifyCanRunChanged();
            }
        }
    }

    /// <summary>True während des Tool-Downloads.</summary>
    public bool IsDownloadingTool
    {
        get => _isDownloadingTool;
        private set
        {
            if (SetProperty(ref _isDownloadingTool, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(HasDownloadError));
                NotifyCanRunChanged();
            }
        }
    }

    public string DownloadMessage
    {
        get => _downloadMessage;
        private set
        {
            if (SetProperty(ref _downloadMessage, value))
                OnPropertyChanged(nameof(HasDownloadError));
        }
    }

    /// <summary>True when the automatic download failed and no tool is ready to run.</summary>
    public bool HasDownloadError => !_isDownloadingTool && !string.IsNullOrWhiteSpace(_downloadMessage);

    /// <summary>
    /// Rohausgabe des Tools samt Exit-Code. Ohne sie bleibt jeder Fehlschlag ein Rätsel: das Tool
    /// meldet den Grund (Steam nicht gestartet, Konto besitzt das Spiel nicht, Tageslimit) auf
    /// stdout/stderr — nicht die App.
    /// </summary>
    public string ToolOutput
    {
        get => _toolOutput;
        private set
        {
            if (SetProperty(ref _toolOutput, value))
                OnPropertyChanged(nameof(HasToolOutput));
        }
    }

    public bool HasToolOutput => !string.IsNullOrWhiteSpace(_toolOutput);

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>True solange kein Ticket-Vorgang läuft — die Eingabe bleibt sonst editierbar.</summary>
    public bool IsIdle => !_isBusy;

    public bool CanRun => !IsBusy && !IsDownloadingTool && !string.IsNullOrWhiteSpace(ToolExecutablePath) && ResolveAppId() is not null;

    /// <summary>Notifies the button that its CanExecute changed (tool ready / selection changed).</summary>
    public void NotifyCanRunChanged() => RunCommand.NotifyCanExecuteChanged();

    public IAsyncRelayCommand RunCommand { get; }
    public ICommand ClearResultCommand { get; }
    public ICommand BrowseToolCommand { get; }

    /// <summary>Stellt sicher, dass das Tool heruntergeladen ist. Wird einmal beim Laden der Page ausgeführt.</summary>
    public async Task EnsureToolDownloadedAsync()
    {
        // Ein bereits gecachtes Tool wird direkt benutzt: ein erneuter Download würde offline
        // fehlschlagen und dabei einen funktionierenden lokalen Stand wegwerfen.
        if (!string.IsNullOrWhiteSpace(ToolExecutablePath) && File.Exists(ToolExecutablePath))
        {
            DownloadMessage = string.Empty;
            Status = "AppID eingeben und Ticket holen.";
            NotifyCanRunChanged();
            return;
        }

        if (_downloader.HasCachedExecutable && !string.IsNullOrWhiteSpace(_downloader.CachedPath))
        {
            ToolExecutablePath = _downloader.CachedPath;
            DownloadMessage = string.Empty;
            Status = "AppID eingeben und Ticket holen.";
            NotifyCanRunChanged();
            return;
        }

        try
        {
            Status = "Lade das Ticket-Generator-Tool …";
            IsDownloadingTool = true;
            var result = await _downloader.DownloadLatestAsync();

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.CachedPath) && File.Exists(result.CachedPath))
            {
                ToolExecutablePath = result.CachedPath;
                DownloadMessage = string.Empty;
                IsDownloadingTool = false;
                Status = "AppID eingeben und Ticket holen.";
                NotifyCanRunChanged();
                _logging.Add(SteamContentManager.Models.LogLevel.Info, "DenuvoActivation",
                    $"Ticket-Generator bereitgestellt: {result.CachedPath}");
            }
            else
            {
                IsDownloadingTool = false;
                DownloadMessage = result.Message;
                Status = $"Tool konnte nicht heruntergeladen werden: {result.Message}";
                NotifyCanRunChanged();
                _logging.Add(SteamContentManager.Models.LogLevel.Warning, "DenuvoActivation",
                    $"Tool-Download fehlgeschlagen: {result.Message}");
            }
        }
        catch (Exception exception)
        {
            IsDownloadingTool = false;
            DownloadMessage = $"Das Tool konnte nicht heruntergeladen werden: {exception.Message}";
            Status = DownloadMessage;
            NotifyCanRunChanged();
            _logging.Add(SteamContentManager.Models.LogLevel.Error, "DenuvoActivation",
                $"Tool-Download fehlgeschlagen: {exception.Message}");
        }
    }

    /// <summary>Bestimmt die zu verwendende AppID: ausgewähltes Spiel oder manuelle Eingabe.</summary>
    private int? ResolveAppId()
    {
        if (_selectedGame is not null)
            return _selectedGame.AppId;

        if (int.TryParse(_manualAppId, out var manualId) && manualId > 0)
            return manualId;

        return null;
    }

    /// <summary>Setzt die manuelle AppID aus dem ausgewählten Spiel.</summary>
    private void UpdateManualAppIdFromGame()
    {
        if (_selectedGame is not null)
            ManualAppId = _selectedGame.AppId.ToString();
    }

    /// <summary>Markiert ein Spiel als ausgewählt, wenn die manuelle AppID einer entspricht.</summary>
    private void UpdateGameSelectionFromManual()
    {
        if (string.IsNullOrWhiteSpace(_manualAppId))
        {
            _selectedGame = null;
            OnPropertyChanged(nameof(HasGameSelection));
            OnPropertyChanged(nameof(SelectedAppIdLabel));
            return;
        }

        if (int.TryParse(_manualAppId, out var id))
        {
            var match = _store.Games.FirstOrDefault(g => g.AppId == id);
            if (match is not null)
                _selectedGame = match;
        }

        OnPropertyChanged(nameof(HasGameSelection));
        OnPropertyChanged(nameof(SelectedAppIdLabel));
    }

    private void ClearResult()
    {
        _hasResult = false;
        TicketShort = string.Empty;
        SteamIdDisplay = string.Empty;
        ConfigInfo = string.Empty;
        Status = "AppID eingeben und Ticket holen.";
        OnPropertyChanged(nameof(HasResult));
    }

    private void BrowseToolCommandExecuted()
    {
        var ofd = new OpenFileDialog();
        ofd.Filter = "Steam-Ticket-Generator|steam-ticket-generator.exe|Alle Dateien|*.*";
        ofd.Title = "Steam-Ticket-Generator auswählen";
        if (!string.IsNullOrEmpty(ToolExecutablePath))
            ofd.InitialDirectory = Path.GetDirectoryName(ToolExecutablePath);
        if (ofd.ShowDialog() == true)
        {
            ToolExecutablePath = ofd.FileName;
            DownloadMessage = string.Empty;
            Status = "AppID eingeben und Ticket holen.";
            NotifyCanRunChanged();
        }
    }

    private async Task RunAsync()
    {
        var appId = ResolveAppId();
        if (appId is null || string.IsNullOrWhiteSpace(ToolExecutablePath))
            return;

        if (!File.Exists(ToolExecutablePath))
        {
            Status = $"Das Tool wurde nicht gefunden: {ToolExecutablePath}";
            return;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        IsBusy = true;
        HasResult = false;
        ToolOutput = string.Empty;
        TicketShort = string.Empty;
        SteamIdDisplay = string.Empty;
        ConfigInfo = string.Empty;

        // Das Tool wird mit dem Cache-Ordner als Working Directory ausgeführt, damit
        // steam_api64.dll gefunden wird.
        var workingDirectory = Path.GetDirectoryName(ToolExecutablePath) ?? string.Empty;
        Status = $"Ticket wird abgeholt für App {appId.Value} …";

        try
        {
            // Das Tool hat keine CLI-Flags: es fragt die AppID interaktiv ab ("Enter the App ID")
            // und wartet danach auf zwei Enter — für configs.user.ini und für "Press Enter to exit".
            // Ohne diese Antworten blockiert der Prozess auf stdin und es passiert nichts.
            var standardInput = string.Concat(
                appId.Value.ToString(),
                Environment.NewLine,
                Environment.NewLine,
                Environment.NewLine);

            var request = new LocalToolRunRequest(ToolExecutablePath, string.Empty, workingDirectory, standardInput);
            var result = await _runner.RunAsync(request, _runCts.Token);

            if (_runCts.Token.IsCancellationRequested)
            {
                Status = "Abgebrochen.";
                return;
            }

            ToolOutput = DescribeToolRun(result);

            var parsed = ParseToolOutput(result.Output);
            if (parsed is null)
            {
                Status = result.ExitCode is null or 0
                    ? "Ticket konnte nicht ausgelesen werden — die Tool-Ausgabe unten zeigt, was das Tool gemeldet hat."
                    : $"Das Tool endete mit Exit-Code {result.ExitCode}. Die Tool-Ausgabe unten zeigt den Grund.";
                TicketShort = string.Empty;
                SteamIdDisplay = string.Empty;
                ConfigInfo = string.Empty;
                return;
            }

            var (steamId, ticket) = parsed.Value;
            SteamIdDisplay = steamId;
            TicketShort = FormatTicketShort(ticket);

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var configPath = Path.Combine(desktopPath, "config.user.ini");

            try
            {
                WriteConfigUserIni(configPath, steamId, appId.Value, ticket);
                ConfigInfo = $"config.user.ini erstellt: {configPath}";
                Status = $"Ticket für App {appId.Value} erhalten. config.user.ini auf Desktop geschrieben.";
            }
            catch (Exception writeException)
            {
                ConfigInfo = $"config.user.ini konnte nicht geschrieben werden: {writeException.Message}";
                Status = $"Ticket für App {appId.Value} erhalten, aber die INI konnte nicht auf den Desktop geschrieben werden.";
            }

            HasResult = true;
            _logging.Add(SteamContentManager.Models.LogLevel.Info, "DenuvoActivation",
                $"Ticket für App {appId.Value} erzeugt. SteamID={steamId}, TicketLen={ticket.Length}.");
        }
        catch (OperationCanceledException)
        {
            Status = "Abgebrochen.";
            return;
        }
        catch (Exception exception)
        {
            Status = $"Fehler beim Ticket-Vorgang: {exception.Message}";
            _logging.Add(SteamContentManager.Models.LogLevel.Error, "DenuvoActivation",
                $"Ticket-Vorgang fehlgeschlagen für App {appId.Value}: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// Versucht, SteamID und EncryptedAppTicket aus der Tool-Ausgabe zu parsen.
    /// Das erwartete Format ist wie beim Rust-Projekt:
    ///   Steam ID: <steamid>
    ///   Encrypted App Ticket: <base64>
    /// </summary>
    private static (string steamId, string ticket)? ParseToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var steamIdMatch = Regex.Match(output, @"Steam ID:\s*(\d+)", RegexOptions.IgnoreCase);
        var ticketMatch = Regex.Match(output, @"Encrypted App Ticket:\s*([A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase);

        if (!steamIdMatch.Success || !ticketMatch.Success)
            return null;

        var steamId = steamIdMatch.Groups[1].Value;
        var ticket = ticketMatch.Groups[1].Value.Trim();

        if (string.IsNullOrWhiteSpace(steamId) || string.IsNullOrWhiteSpace(ticket))
            return null;

        return (steamId, ticket);
    }

    /// <summary>Rohausgabe des Tools zusammen mit Exit-Code — die einzige ehrliche Fehlerquelle.</summary>
    private static string DescribeToolRun(LocalToolRunResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Output) ? "(keine Ausgabe)" : result.Output.Trim();
        var exitCode = result.ExitCode?.ToString() ?? "—";
        return $"Exit-Code: {exitCode} · {result.Message}{Environment.NewLine}{Environment.NewLine}{text}";
    }

    /// <summary>Erstellt die config.user.ini auf dem Desktop.</summary>
    private static void WriteConfigUserIni(string path, string steamId, int appId, string ticket)
    {
        var lines = new StringBuilder();
        lines.AppendLine("[user::general]");
        lines.AppendLine($"account_steamid={steamId}");
        lines.AppendLine($"ticket={ticket}");
        lines.AppendLine();
        lines.AppendLine(string.Format("# Generiert durch ResonanceTools – Denuvo Activation (AppID {0}).", appId));

        File.WriteAllText(path, lines.ToString(), Encoding.UTF8);
    }

    private static string FormatTicketShort(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return string.Empty;

        var len = ticket.Length;
        var head = ticket.Length > 48 ? ticket[..48] : ticket;
        return $"{head}… ({len} Zeichen)";
    }
}
