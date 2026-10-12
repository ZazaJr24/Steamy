using System.Diagnostics;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public sealed record GameLaunchResult(bool Succeeded, string Message);
public interface IGameProcessStarter { void Start(ProcessStartInfo request); }
public sealed class GameProcessStarter : IGameProcessStarter
{
    public void Start(ProcessStartInfo request) { using var process = Process.Start(request); }
}
public interface IGameLaunchService
{
    Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default);
}
public sealed class GameLaunchService(IAppDataStore store, ISettingsService settings, IPlayTimeService playtime,
    IGameActivityService activity, IGamePreferencesStore preferences, IGameProcessStarter starter, ILoggingService logging) : IGameLaunchService
{
    public async Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default)
    {
        if (game.AppId == 0 || game.InstallState != GameInstallState.Installed
            || !store.Games.Any(g => g.AppId == game.AppId && g.InstallState == GameInstallState.Installed))
            return new(false, "This game is no longer installed. Refresh your library.");
        try
        {
            var options = await preferences.ReadAsync(game.AppId, token);
            var steamRoot = settings.Load().SteamLibraryPath;
            if (!game.IsLocalDownload && !string.IsNullOrWhiteSpace(options.LaunchArguments)
                && !File.Exists(Path.Combine(steamRoot ?? "", "steam.exe"))) steamRoot = SteamLibraryService.FindSteamRoot();
            var request = BuildRequest(game, options.LaunchArguments, steamRoot, options.LaunchExecutable);
            if (request is null) return new(false, "The game or Steam executable is missing. Check its installation folder in Settings.");
            await playtime.ConfigureAsync(settings.Load(), store.Games);
            token.ThrowIfCancellationRequested();
            starter.Start(request);
            if (game.AppId != 0)
            {
                try { await activity.RecordLaunchAsync(game.AppId, cancellationToken: token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { logging.Add(LogLevel.Warning, "Library", "The game launch was requested, but its history could not be saved."); }
            }
            return new(true, "Launch requested. Playtime updates when the game is running.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            logging.Add(LogLevel.Warning, "Library", "The game could not be started. Check its installation and launch options.");
            return new(false, "Could not start the game. Check its installation and launch options.");
        }
    }
    internal static bool IsGameExecutable(Game game, string executable)
    {
        try
        {
            var root = Path.GetFullPath(game.InstallFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.IsPathFullyQualified(executable) && Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(executable);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    internal static ProcessStartInfo? BuildRequest(Game game, string arguments, string? steamRoot = null, string? executableOverride = null)
    {
        if (game.IsLocalDownload)
        {
            var executable = string.IsNullOrWhiteSpace(executableOverride) ? game.LaunchExecutable : executableOverride;
            if (!IsGameExecutable(game, executable)) return null;
            return new(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)!, Arguments = arguments };
        }
        if (game.AppId <= 0) return null;
        if (string.IsNullOrWhiteSpace(arguments)) return new($"steam://rungameid/{game.AppId}") { UseShellExecute = true };
        var steam = Path.Combine(steamRoot ?? "", "steam.exe");
        if (!File.Exists(steam)) return null;
        return new(steam) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(steam))!, Arguments = $"-applaunch {game.AppId} " + arguments };
    }
}
