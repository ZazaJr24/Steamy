using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Steamy.Models;
using Steamy.Playtime;

namespace Steamy.Services;

public interface IPlayTimeService
{
    Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default);
    Task<DateTimeOffset?> ReadLastPlayedAsync(int appId, CancellationToken token = default) => Task.FromResult<DateTimeOffset?>(null);
    Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games);
}

/// <summary>UI bridge to the independently running, single-instance background tracker.</summary>
public sealed class PlayTimeService(ILoggingService logging, ISettingsService settings) : IPlayTimeService
{
    private readonly SemaphoreSlim _gate = new(1);
    private string? _configuration;
    private DateTimeOffset _lastStart;
    private DateTimeOffset _lastSteamRead;
    private SteamPlayTimes? _steamTimes;
    private string? _steamRoot;
    private readonly SemaphoreSlim _readGate = new(1);
    private string? _collectorBuild;
    private bool _collectorChecked;

    public Task<DateTimeOffset?> ReadLastPlayedAsync(int appId, CancellationToken token = default) =>
        Task.FromResult(_steamTimes?.LastPlayed.TryGetValue(appId, out var date) == true ? (DateTimeOffset?)date : null);

    public async Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default)
    {
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = await PlayTimeFiles.ReadAsync<PlayTimeSnapshot>(Path.Combine(PlayTimeFiles.DefaultDirectory, PlayTimeFiles.TimeFile), token).ConfigureAwait(false);
            var local = snapshot is { SchemaVersion: 1, Games: not null } ? snapshot : PlayTimeSnapshot.Empty;
            var root = settings.Load().SteamLibraryPath;
            if (string.IsNullOrWhiteSpace(root)) root = SteamLibraryService.FindSteamRoot() ?? "";
            if (root != _steamRoot) { _steamRoot = root; _steamTimes = null; _lastSteamRead = DateTimeOffset.MinValue; }
            if (DateTimeOffset.UtcNow - _lastSteamRead > TimeSpan.FromSeconds(30))
            {
                _steamTimes = await Task.Run(() => SteamPlayTimeReader.Read(root), token).ConfigureAwait(false) ?? _steamTimes;
                _lastSteamRead = DateTimeOffset.UtcNow;
            }
            if (_steamTimes is null) return local;
            var importPath = Path.Combine(PlayTimeFiles.DefaultDirectory, "steam-playtime-" + _steamTimes.Account + ".json");
            var previous = await PlayTimeFiles.ReadAsync<PlayTimeImportState>(importPath, token).ConfigureAwait(false);
            var merged = SteamPlayTimeMerger.Merge(local, _steamTimes, previous);
            if (previous is null || previous.Account != merged.State.Account || !previous.Games.SequenceEqual(merged.State.Games))
                await PlayTimeFiles.WriteAsync(importPath, merged.State, token).ConfigureAwait(false);
            return merged.Snapshot;
        }
        finally { _readGate.Release(); }
    }

    public async Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games)
    {
        // Materialize UI collections before leaving the dispatcher.
        var installed = games.Where(g => g.AppId != 0 && g.InstallState == GameInstallState.Installed && !string.IsNullOrWhiteSpace(g.InstallFolder))
            .Select(g => new TrackedGame(g.AppId, g.InstallFolder)).DistinctBy(g => g.AppId).Take(5000).OrderBy(g => g.AppId).ToArray();
        var configuration = new TrackerConfiguration(settings.TrackPlayTime, settings.SteamLibraryPath, installed);
        var json = JsonSerializer.Serialize(configuration);
        await _gate.WaitAsync();
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "PlayTimeTracker.exe");
            var collectorDll = Path.Combine(AppContext.BaseDirectory, "PlayTimeTracker.dll");
            var buildPath = Path.Combine(PlayTimeFiles.DefaultDirectory, "collector-build.txt");
            if (configuration.Enabled && !_collectorChecked && File.Exists(collectorDll))
            {
                _collectorBuild ??= Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(collectorDll)));
                var previousBuild = File.Exists(buildPath) ? await File.ReadAllTextAsync(buildPath) : "";
                if (previousBuild != _collectorBuild && CollectorIsRunning())
                {
                    // Let the older worker flush and exit normally before starting the new binary.
                    await PlayTimeFiles.WriteAsync(Path.Combine(PlayTimeFiles.DefaultDirectory, PlayTimeFiles.ConfigurationFile), configuration with { Enabled = false });
                    for (var attempt = 0; attempt < 40 && CollectorIsRunning(); attempt++) await Task.Delay(250);
                    if (CollectorIsRunning())
                    {
                        await PlayTimeFiles.WriteAsync(Path.Combine(PlayTimeFiles.DefaultDirectory, PlayTimeFiles.ConfigurationFile), configuration);
                        logging.Add(LogLevel.Warning, "Playtime", "The previous tracker is still exiting; its update will be retried.");
                        return;
                    }
                    _configuration = null;
                }
                Directory.CreateDirectory(PlayTimeFiles.DefaultDirectory);
                await File.WriteAllTextAsync(buildPath, _collectorBuild);
                _collectorChecked = true;
            }
            if (json != _configuration)
            {
                await PlayTimeFiles.WriteAsync(Path.Combine(PlayTimeFiles.DefaultDirectory, PlayTimeFiles.ConfigurationFile), configuration);
                _configuration = json;
            }
            if (!configuration.Enabled) { _lastStart = DateTimeOffset.MinValue; return; }
            if (DateTimeOffset.UtcNow - _lastStart < TimeSpan.FromSeconds(20)) return;
            if (!File.Exists(executable))
            {
                logging.Add(LogLevel.Warning, "Playtime", "PlayTimeTracker.exe is missing from the Steamy folder.");
                return;
            }
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { "--data-dir", PlayTimeFiles.DefaultDirectory }
            });
            _lastStart = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { logging.Add(LogLevel.Warning, "Playtime", $"The background tracker could not start: {e.Message}"); }
        finally { _gate.Release(); }
    }

    private static bool CollectorIsRunning()
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlayTimeFiles.DefaultDirectory.ToUpperInvariant())))[..24];
        try
        {
            using var mutex = Mutex.OpenExisting(@"Local\Steamy.PlayTimeTracker." + identity);
            try { if (!mutex.WaitOne(0)) return true; }
            catch (AbandonedMutexException) { }
            mutex.ReleaseMutex();
            return false;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}
