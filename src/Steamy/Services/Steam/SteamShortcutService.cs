using System.Diagnostics;
using System.Globalization;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public sealed record SteamShortcutResult(bool Succeeded, string Message, bool RequiresSteamRestart = false);
public interface ISteamShortcutService
{
    Task<SteamShortcutResult> AddAsync(Game game, string executable, string arguments, bool restartSteam = false, CancellationToken token = default);
}
public interface ISteamShortcutClient
{
    bool IsRunning { get; }
    void RequestExit(string steamRoot);
    void Open(string steamRoot);
}
internal sealed class SteamShortcutClient(IGameProcessStarter starter) : ISteamShortcutClient
{
    public bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("steam");
            try { return processes.Any(process => !process.HasExited); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public void RequestExit(string steamRoot) => Start(steamRoot, "-shutdown");
    public void Open(string steamRoot) => Start(steamRoot, "");
    private void Start(string root, string arguments) => starter.Start(new ProcessStartInfo(Path.Combine(root, "steam.exe"))
    { Arguments = arguments, WorkingDirectory = root, UseShellExecute = true });
}

public sealed class SteamShortcutService(ISettingsService settings, ISteamShortcutClient client) : ISteamShortcutService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<SteamShortcutResult> AddAsync(Game game, string executable, string arguments, bool restartSteam = false, CancellationToken token = default)
    {
        if (game.InstallState != GameInstallState.Installed)
            return new(false, "Install this game before adding it to Steam.");
        if (!game.IsLocalDownload && game.AppId > 0)
            return new(true, "This game is already registered in Steam.");
        if (!GameLaunchService.IsGameExecutable(game, executable))
            return new(false, "Choose the game's executable in Launch options first.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? root = null; var exitRequested = false;
        try
        {
            var configured = settings.Load().SteamLibraryPath;
            root = !string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "steam.exe")) ? configured : SteamLibraryService.FindSteamRoot();
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "steam.exe")))
                return new(false, "Steam was not found. Select its folder in Settings.");
            var account = FindAccount(root);
            if (account is null) return new(false, "Sign in to Steam once so Steamy can find your Steam library.");
            if (client.IsRunning)
            {
                if (!restartSteam) return new(false, "Steam needs to restart to add this game. Your existing shortcuts will be backed up.", true);
                exitRequested = true; client.RequestExit(root);
                var timeout = Stopwatch.StartNew();
                // Once the user approved shutdown, finish observing it even if
                // they leave the page, so a late exit can still reopen Steam.
                while (client.IsRunning && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(200).ConfigureAwait(false);
                if (client.IsRunning) return new(false, "Steam is still running. Close its running games and try again.", true);
            }
            token.ThrowIfCancellationRequested();
            var file = Path.Combine(root, "userdata", account, "config", "shortcuts.vdf");
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (client.IsRunning) throw new IOException("Steam opened before the shortcut could be saved. Try again.");
                return SteamShortcutFile.Add(file, game.Name, executable, arguments);
            }, token).ConfigureAwait(false);
            return new(true, result.Added ? "Added to Steam. You'll find it in your Steam library." : "This game is already in your Steam library.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or System.Security.SecurityException or InvalidOperationException)
        { return new(false, "Could not add the game to Steam. " + error.Message); }
        finally
        {
            try
            {
                if (exitRequested && root is not null && !client.IsRunning) client.Open(root);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            { /* The shortcut is saved even if Steam has to be opened manually. */ }
            _gate.Release();
        }
    }
    internal static string? FindAccount(string root)
    {
        var login = Path.Combine(root, "config", "loginusers.vdf");
        var users = File.Exists(login) ? VdfParser.Parse(File.ReadAllText(login))["users"] : null;
        var latest = users?.Children.OrderByDescending(user => user.GetInt("MostRecent")).ThenByDescending(user => user.GetLong("Timestamp"))
            .Select(user => ulong.TryParse(user.Key, out var id) && (id >> 32) == 17825793 && (uint)id > 0 ? ((uint)id).ToString(CultureInfo.InvariantCulture) : null).FirstOrDefault(id => id is not null);
        if (latest is not null) return latest;
        var data = Path.Combine(root, "userdata");
        if (!Directory.Exists(data)) return null;
        var accounts = Directory.EnumerateDirectories(data).Select(Path.GetFileName).Where(name => uint.TryParse(name, out var id) && id > 0).Take(2).ToArray();
        return accounts.Length == 1 ? accounts[0] : null;
    }
}
