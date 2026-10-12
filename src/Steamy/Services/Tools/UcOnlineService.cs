using System.IO;
using System.Text.Json;
using UCO2.Patcher.Core;
using UcGameScanResult = UCO2.Patcher.Core.GameScanResult;

namespace Steamy.Services;

/// <summary>Uses the upstream patcher's scanner, planner and verified backup transactions.</summary>
public sealed class UcOnlineService(IGitHubToolDownloadService downloads, string? backupRoot = null)
{
    private readonly BackupService _backups = new(backupRoot ?? Path.Combine(Steamy.Storage.AppDataPaths.Root, "UcOnline", "backups"));
    public Task<UcGameScanResult> ScanAsync(string folder, CancellationToken token) => new GameScanner().ScanAsync(folder, token);
    public UcGameScanResult SelectExecutable(UcGameScanResult game, string executable)
    {
        var path = Path.GetFullPath(executable);
        if (!SafeFileSystem.IsInside(game.GameDirectory, path) || !File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose an executable inside this game folder.");
        RejectLinks(path);
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid game executable.");
        stream.Position = 0x3c; var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 24) throw new InvalidDataException("Invalid game executable.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Invalid game executable.");
        var machine = reader.ReadUInt16();
        if (machine != (game.Architecture == GameArchitecture.X64 ? 0x8664 : 0x14c)) throw new InvalidDataException("The executable and Steam API architecture do not match.");
        return new UcGameScanResult { GameDirectory = game.GameDirectory, Engine = game.Engine, Architecture = game.Architecture,
            SteamApiPath = game.SteamApiPath, SteamApiPaths = game.SteamApiPaths, ExecutablePath = path, ConfigDirectory = Path.GetDirectoryName(path)!,
            UnityDataDirectory = game.UnityDataDirectory, Backends = game.Backends, SteamStub = PeInspector.DetectSteamStub(path), CompetingFiles = game.CompetingFiles, Warnings = game.Warnings };
    }
    public Task<IReadOnlyList<BackupSnapshot>> BackupsAsync(string folder, CancellationToken token) => Task.Run(() => _backups.ListAsync(folder, token), token);
    public async Task<PatchPlan> PlanAsync(UcGameScanResult game, PatchOptions options, IProgress<string>? progress, CancellationToken token)
    {
        var copy = JsonSerializer.Deserialize<PatchOptions>(JsonSerializer.Serialize(options))!;
        var missing = BackendSettingsValidator.FindMissing(game, copy);
        if (missing.Count > 0) throw new InvalidOperationException("Complete the backend settings: " + string.Join("; ", missing.Select(item => item.Backend + ": " + string.Join(", ", item.Fields))));
        if (copy.AppId == 0 || copy.OriginalAppId == 0) throw new InvalidOperationException("Enter valid game and network App IDs.");
        var dll = await Task.Run(() => downloads.GetCachedPath(ToolDefinitions.UcOnline), token).ConfigureAwait(false);
        if (dll is null)
        {
            progress?.Report("Downloading and verifying UC Online…");
            var result = await downloads.DownloadLatestAsync(ToolDefinitions.UcOnline, token).ConfigureAwait(false);
            if (!result.Succeeded || result.CachedPath is null) throw new IOException(result.Message);
            dll = result.CachedPath;
        }
        var packageRoot = Path.GetDirectoryName(dll)!;
        if (Path.GetFileName(packageRoot).Equals("x64", StringComparison.OrdinalIgnoreCase)) packageRoot = Path.GetDirectoryName(packageRoot)!;
        return await Task.Run(() =>
        {
        var plan = new PatchPlanner(new ArtifactLocator(packageRoot)).Create(game, copy);
        foreach (var operation in plan.Operations)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeFileSystem.IsInside(game.GameDirectory, operation.TargetPath)) throw new IOException("A target is outside the selected game folder.");
            RejectLinks(operation.TargetPath);
            if (operation.SourcePath is { } source)
            {
                RejectLinks(source);
                if (!SafeFileSystem.IsInside(packageRoot, source)) throw new IOException("A tool file is outside its verified package.");
            }
        }
        return plan;
        }, token).ConfigureAwait(false);
    }
    public Task<BackupManifest> ApplyAsync(PatchPlan plan, IProgress<string>? progress, CancellationToken token)
    {
        foreach (var operation in plan.Operations) RejectLinks(operation.TargetPath);
        return _backups.ApplyAsync(plan, progress, token);
    }
    public async Task RestoreAsync(string manifestPath, string gameFolder, IProgress<string>? progress, CancellationToken token)
    {
        await Task.Run(async () =>
        {
        if (!SafeFileSystem.IsInside(_backups.StorageRoot, manifestPath)) throw new IOException("The backup is outside Steamy's backup folder.");
        RejectLinks(manifestPath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid backup.");
        if (!Path.GetFullPath(manifest.GameDirectory).Equals(Path.GetFullPath(gameFolder), StringComparison.OrdinalIgnoreCase)) throw new IOException("The backup belongs to another game.");
        foreach (var entry in manifest.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeFileSystem.IsInside(gameFolder, entry.TargetPath)) throw new IOException("A backup target is outside the game folder.");
            RejectLinks(entry.TargetPath);
            if (entry.BackupRelativePath is { } relative)
            {
                var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, relative));
                if (!SafeFileSystem.IsInside(Path.GetDirectoryName(manifestPath)!, source)) throw new IOException("Invalid backup source.");
                RejectLinks(source);
                if (!File.Exists(source) || !SafeFileSystem.Sha256(source).Equals(entry.OriginalHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup verification failed. Original files were not changed: " + Path.GetFileName(entry.TargetPath));
            }
            if (entry.ReplacementHash is { } expected && File.Exists(entry.TargetPath) && !SafeFileSystem.Sha256(entry.TargetPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A patched file changed since installation. Restore was stopped to preserve it: " + Path.GetFileName(entry.TargetPath));
        }
        await _backups.RestoreAsync(manifestPath, progress, token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Choose a regular folder without symbolic links.");
    }
}
