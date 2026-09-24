using System.Diagnostics;
using System.IO;

namespace SteamContentManager.Services;

/// <summary>
/// Which Steamless build is selected and what it should unpack. The tool always runs without a
/// window of its own; its output is captured so the application can show it itself.
/// </summary>
public sealed record SteamlessRequest(
    string SteamlessPath,
    string TargetExePath,
    string ExtraArguments = "");

public sealed record SteamlessToolStatus(
    bool IsReady,
    string ExecutablePath,
    string Message,
    string? Version,
    DateTime CheckedAt);

public sealed record SteamlessRunResult(
    bool Succeeded,
    bool Unpacked,
    string Message,
    string? TargetExePath,
    string? BackupPath,
    int? ExitCode,
    bool WasCancelled)
{
    /// <summary>Everything Steamless wrote to stdout/stderr, so the page can show it.</summary>
    public string Output { get; init; } = string.Empty;

    public static SteamlessRunResult Failure(string message, string? targetExePath = null, int? exitCode = null)
        => new(false, false, message, targetExePath, null, exitCode, false);
}

/// <summary>
/// The Steamless build that ships with the application. It lives next to the executable in
/// <c>Tools\Steamless</c>, so the workflow works without any download. The plugins folder must sit
/// beside the CLI: without it Steamless cannot unpack anything and fails at start-up.
/// </summary>
public static class SteamlessBundle
{
    public const string FolderName = "Steamless";
    public const string CliFileName = "Steamless.CLI.exe";
    public const string PluginsFolderName = "Plugins";

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Tools", FolderName);

    public static string CliPath => Path.Combine(DirectoryPath, CliFileName);

    /// <summary>True when the bundled build is complete and can be started.</summary>
    public static bool IsAvailable => File.Exists(CliPath) && Directory.Exists(PluginsDirectoryPath);

    public static string PluginsDirectoryPath => Path.Combine(DirectoryPath, PluginsFolderName);
}

public interface ISteamlessService
{
    /// <summary>Checks that the selected Steamless build can be started.</summary>
    Task<SteamlessToolStatus> CheckAsync(string steamlessPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs Steamless on the target executable and then swaps the result in:
    /// Steamless writes <c>&lt;name&gt;.unpacked.exe</c> next to the input, so afterwards the
    /// original becomes <c>&lt;name&gt;.bak.exe</c> and the unpacked build takes the original name.
    /// Nothing is renamed when Steamless produced no unpacked file.
    /// </summary>
    Task<SteamlessRunResult> RunAsync(SteamlessRequest request, CancellationToken cancellationToken = default);

    /// <summary>Puts the backup back and moves the current build aside, so the swap is reversible.</summary>
    /// <remarks>
    /// Steamless writes its result as <c>&lt;name&gt;.exe.unpacked.exe</c> (the whole file name plus
    /// <c>.unpacked</c>). Runs of other builds that use <c>&lt;name&gt;.unpacked.exe</c> are
    /// recognised too, so a leftover from an older attempt is never mistaken for a missing one.
    /// </remarks>
    Task<SteamlessRunResult> UndoAsync(string targetExePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// The file swapping half of the Steamless workflow, kept free of process handling so it can be
/// tested directly: given the original exe and Steamless' <c>.unpacked.exe</c> output, it makes the
/// unpacked build the real one and keeps the original as a backup.
/// </summary>
public static class SteamlessFileSwap
{
    /// <summary>
    /// Where Steamless writes its result. It appends <c>.unpacked</c> to the <b>whole</b> file name,
    /// so <c>game.exe</c> becomes <c>game.exe.unpacked.exe</c> — not <c>game.unpacked.exe</c>.
    /// Getting this wrong means a successful run looks like a failure, which is exactly what
    /// happened once; the name is therefore verified against the real tool in SteamlessBundleTests.
    /// </summary>
    public static string UnpackedPathFor(string targetExePath)
    {
        var full = Path.GetFullPath(targetExePath);
        var directory = Path.GetDirectoryName(full)!;
        return Path.Combine(directory, Path.GetFileName(full) + ".unpacked" + Path.GetExtension(full));
    }

    /// <summary>The name older Steamless builds used, kept so leftovers are still recognised.</summary>
    public static string LegacyUnpackedPathFor(string targetExePath)
    {
        var full = Path.GetFullPath(targetExePath);
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        return Path.Combine(directory, $"{name}.unpacked{extension}");
    }

    /// <summary>The unpacked file that actually exists, whichever naming the tool used.</summary>
    public static string? ExistingUnpackedPathFor(string targetExePath)
    {
        var current = UnpackedPathFor(targetExePath);
        if (File.Exists(current)) return current;

        var legacy = LegacyUnpackedPathFor(targetExePath);
        return File.Exists(legacy) ? legacy : null;
    }

    public static string BackupPathFor(string targetExePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetExePath))!;
        var name = Path.GetFileNameWithoutExtension(targetExePath);
        var extension = Path.GetExtension(targetExePath);
        return Path.Combine(directory, $"{name}.bak{extension}");
    }

    public static bool HasUnpackedResult(string targetExePath) => ExistingUnpackedPathFor(targetExePath) is not null;

    /// <summary>Backs the original up and lets the unpacked build take its place.</summary>
    public static bool TryApplyUnpackedResult(string targetExePath, out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);

        var target = Path.GetFullPath(targetExePath);
        var backup = BackupPathFor(target);

        if (!File.Exists(target))
        {
            message = $"The target executable no longer exists: {target}";
            return false;
        }

        var unpacked = ExistingUnpackedPathFor(target);
        if (unpacked is null)
        {
            message = $"Steamless produced no unpacked file, so nothing was renamed. Expected: {UnpackedPathFor(target)}";
            return false;
        }

        try
        {
            // Never destroy an earlier backup silently: keep it under a numbered name instead.
            if (File.Exists(backup))
            {
                var alternative = NextFreeBackupName(backup);
                File.Move(backup, alternative);
                message = string.Empty;
            }

            File.Move(target, backup);
            File.Move(unpacked, target);

            message = $"Done: {Path.GetFileName(target)} was kept as {Path.GetFileName(backup)} and {Path.GetFileName(unpacked)} now runs as {Path.GetFileName(target)}.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"The rename step failed: {exception.Message}";
            return false;
        }
    }

    /// <summary>Restores the backup and moves the current build back to the unpacked name.</summary>
    public static bool TryUndo(string targetExePath, out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);

        var target = Path.GetFullPath(targetExePath);
        var unpacked = UnpackedPathFor(target);
        var backup = BackupPathFor(target);

        if (!File.Exists(backup))
        {
            message = $"No backup was found, so there is nothing to restore. Expected: {backup}";
            return false;
        }

        try
        {
            // The previous build is kept aside under the name Steamless itself would have used.
            if (File.Exists(target)) File.Move(target, unpacked, overwrite: true);
            File.Move(backup, target);

            message = $"Undone: {Path.GetFileName(backup)} is the executable again and the previous build is kept as {Path.GetFileName(unpacked)}.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"The undo step failed: {exception.Message}";
            return false;
        }
    }

    private static string NextFreeBackupName(string backup)
    {
        var directory = Path.GetDirectoryName(backup)!;
        var name = Path.GetFileNameWithoutExtension(backup);
        var extension = Path.GetExtension(backup);

        for (var index = 2; index < 100; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }
}

public sealed class SteamlessService : ISteamlessService
{
    public Task<SteamlessToolStatus> CheckAsync(string steamlessPath, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.Now;

        // Out of the box the bundled build is used; the user only picks a file to override it.
        if (string.IsNullOrWhiteSpace(steamlessPath))
        {
            return SteamlessBundle.IsAvailable
                ? Task.FromResult(DescribeBundled(checkedAt))
                : Task.FromResult(new SteamlessToolStatus(false, string.Empty, "No bundled Steamless build was found and no build was selected.", null, checkedAt));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(steamlessPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(new SteamlessToolStatus(false, steamlessPath, "That path is not valid.", null, checkedAt));
        }

        if (!File.Exists(fullPath))
            return Task.FromResult(new SteamlessToolStatus(false, fullPath, "The selected Steamless executable was not found.", null, checkedAt));

        var version = GetFileVersionOrEmpty(fullPath);
        var name = Path.GetFileName(fullPath);

        // Steamless ships a GUI (Steamless.exe) and a command line build (Steamless.CLI.exe). This
        // page drives the command line build; the GUI ignores arguments and would just open a
        // window, so a GUI selection is reported instead of failing somewhere later.
        var isGuiBuild = name.Equals("Steamless.exe", StringComparison.OrdinalIgnoreCase);
        var message = isGuiBuild
            ? "This is the GUI build (Steamless.exe) — it ignores command line arguments. Use the bundled build or pick Steamless.CLI.exe."
            : $"Ready: {name}";

        return Task.FromResult(new SteamlessToolStatus(!isGuiBuild, fullPath, message, version, checkedAt));
    }

    /// <summary>Status for the build that ships with the application.</summary>
    private static SteamlessToolStatus DescribeBundled(DateTime checkedAt)
    {
        var path = SteamlessBundle.CliPath;
        var version = GetFileVersionOrEmpty(path);
        return new SteamlessToolStatus(true, path, $"Bundled Steamless build is ready ({SteamlessBundle.CliFileName}).", version, checkedAt);
    }

    /// <summary>Version lookup that never throws — an unreadable file must not break the page.</summary>
    private static string GetFileVersionOrEmpty(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public async Task<SteamlessRunResult> RunAsync(SteamlessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TargetExePath))
            return SteamlessRunResult.Failure("No target executable was selected.");

        string steamlessPath;
        string targetPath;
        try
        {
            steamlessPath = ResolveSteamlessPath(request.SteamlessPath) ?? string.Empty;
            targetPath = Path.GetFullPath(request.TargetExePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SteamlessRunResult.Failure("One of the selected paths is not valid.");
        }

        if (string.IsNullOrWhiteSpace(steamlessPath))
            return SteamlessRunResult.Failure("No Steamless build was found: neither the bundled build nor a selected file is usable.");

        if (!File.Exists(steamlessPath))
            return SteamlessRunResult.Failure("The selected Steamless executable was not found.", targetPath);

        if (!File.Exists(targetPath))
            return SteamlessRunResult.Failure("The target executable was not found.", targetPath);

        if (string.Equals(steamlessPath, targetPath, StringComparison.OrdinalIgnoreCase))
            return SteamlessRunResult.Failure("The Steamless tool and the target executable must be different files.", targetPath);

        var arguments = $"\"{targetPath}\"";
        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments = $"{arguments} {request.ExtraArguments.Trim()}";

        // The tool runs without a window of its own. Everything it writes is captured and shown
        // inside the application instead, so no separate console or dialog is needed.
        var startInfo = new ProcessStartInfo
        {
            FileName = steamlessPath,
            Arguments = arguments,
            // The bundled build loads its unpackers relative to the target executable's working
            // directory only for the file it writes; the tool itself runs from wherever it is.
            WorkingDirectory = Path.GetDirectoryName(targetPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return SteamlessRunResult.Failure("Steamless could not be started.", targetPath);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Both streams are drained while the tool runs: a talkative build would otherwise
            // block on a full pipe and look like a hang.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var stdout = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
            var toolOutput = CombineOutput(stdout, stderr);

            var exitCode = process.HasExited ? process.ExitCode : (int?)null;

            if (cancellationToken.IsCancellationRequested)
                return new SteamlessRunResult(false, false, "The run was cancelled.", targetPath, null, exitCode, true)
                {
                    Output = toolOutput
                };

            // Steamless names its result <file>.unpacked.exe; both namings are accepted so runs
            // from other builds behave the same way.
            if (!SteamlessFileSwap.HasUnpackedResult(targetPath))
            {
                var detail = exitCode is null ? string.Empty : $" (Steamless exit code {exitCode})";
                return new SteamlessRunResult(
                    false,
                    false,
                    $"Steamless finished{detail} but produced no unpacked file next to the target, so nothing was renamed. " +
                    $"Expected {Path.GetFileName(SteamlessFileSwap.UnpackedPathFor(targetPath))} in {Path.GetDirectoryName(targetPath)}. " +
                    "The log below shows what the tool reported — open it with the Log button.",
                    targetPath,
                    null,
                    exitCode,
                    false)
                {
                    Output = toolOutput
                };
            }

            if (!SteamlessFileSwap.TryApplyUnpackedResult(targetPath, out var message))
                return new SteamlessRunResult(false, false, message, targetPath, null, exitCode, false)
                {
                    Output = toolOutput
                };

            return new SteamlessRunResult(
                true,
                true,
                message,
                targetPath,
                SteamlessFileSwap.BackupPathFor(targetPath),
                exitCode,
                false)
            {
                Output = toolOutput
            };
        }
        catch (OperationCanceledException)
        {
            return new SteamlessRunResult(false, false, "The run was cancelled.", targetPath, null, null, true);
        }
        catch (Exception exception)
        {
            return SteamlessRunResult.Failure($"Steamless could not be started: {exception.GetType().Name}.", targetPath);
        }
    }

    /// <summary>
    /// Decides which Steamless build to run. Preference is: an explicitly selected command line
    /// build → the bundled build. A selected GUI build (Steamless.exe) cannot be driven from here,
    /// so the bundled one is used instead of launching a window or failing.
    /// </summary>
    private static string? ResolveSteamlessPath(string? selectedPath)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(selectedPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                candidate = string.Empty;
            }

            var isGuiBuild = Path.GetFileName(candidate).Equals("Steamless.exe", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate) && !isGuiBuild) return candidate;
        }

        return SteamlessBundle.IsAvailable ? SteamlessBundle.CliPath : null;
    }

    /// <summary>Reads a captured stream without letting a cancelled read turn into a failure.</summary>
    private static async Task<string> ReadSafelyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        var parts = new[] { stdout, stderr }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.TrimEnd())
            .ToArray();

        return string.Join(Environment.NewLine, parts);
    }

    public Task<SteamlessRunResult> UndoAsync(string targetExePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetExePath))
            return Task.FromResult(SteamlessRunResult.Failure("No target executable was selected."));

        var target = Path.GetFullPath(targetExePath);
        var backup = SteamlessFileSwap.BackupPathFor(target);

        if (!SteamlessFileSwap.TryUndo(target, out var message))
            return Task.FromResult(SteamlessRunResult.Failure(message, target));

        return Task.FromResult(new SteamlessRunResult(true, false, message, target, backup, null, false));
    }
}
