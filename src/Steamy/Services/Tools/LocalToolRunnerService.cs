using System.Diagnostics;
using System.IO;
using System.Text;

namespace Steamy.Services;

/// <summary>
/// Runs a local, user-selected executable with user-visible arguments and reports what really
/// happened. There is no hidden download and no credential handling: the app only launches the
/// file the user picked and reports the exit code.
/// </summary>
public interface ILocalToolRunner
{
    Task<LocalToolStatus> CheckAsync(string executablePath, string expectedFileNameHint, CancellationToken cancellationToken = default);
    Task<LocalToolRunResult> RunAsync(LocalToolRunRequest request, CancellationToken cancellationToken = default);
}

public sealed record LocalToolRequest(
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory);

/// <summary>
/// Everything needed to run a tool. Tools always run hidden with their output captured, so the
/// application never opens a second window: whatever the tool prints is shown inside the page.
/// <para>
/// <see cref="StandardInput"/> exists because some tools are interactive: the Steam ticket
/// generator asks for the App ID on stdin and then waits for a confirmation. Without a scripted
/// answer the process blocks forever and the page looks like it does nothing.
/// </para>
/// </summary>
public sealed record LocalToolRunRequest(
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string? StandardInput = null,
    bool ShowWindow = false);


public sealed record LocalToolStatus(
    bool IsReady,
    string ExecutablePath,
    string Message,
    string? Version,
    DateTime CheckedAt);

public sealed record LocalToolRunResult(
    bool Succeeded,
    int? ExitCode,
    bool WasCancelled,
    string Message,
    string Output);

public sealed class LocalToolRunnerService : ILocalToolRunner
{
    public Task<LocalToolStatus> CheckAsync(string executablePath, string expectedFileNameHint, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.Now;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            var hint = string.IsNullOrWhiteSpace(expectedFileNameHint) ? "the tool" : expectedFileNameHint;
            return Task.FromResult(new LocalToolStatus(false, string.Empty, $"Select {hint} to enable this workflow.", null, checkedAt));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(new LocalToolStatus(false, executablePath, "The selected path is not valid.", null, checkedAt));
        }

        if (!File.Exists(fullPath))
            return Task.FromResult(new LocalToolStatus(false, fullPath, "The selected executable no longer exists.", null, checkedAt));

        // Reading the version of an arbitrary executable can fail (locked or not a PE image);
        // that must not break the page.
        var version = GetFileVersionOrEmpty(fullPath);
        return Task.FromResult(new LocalToolStatus(true, fullPath, $"Ready: {Path.GetFileName(fullPath)}", version, checkedAt));
    }

    private static async Task<LocalToolRunResult> RunWithVisibleWindowAsync(
        string fullPath, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return new LocalToolRunResult(false, null, false, "The executable could not be started.", string.Empty);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : (int?)null;

            if (cancellationToken.IsCancellationRequested)
                return new LocalToolRunResult(false, exitCode, true, "The run was cancelled.", string.Empty);

            var succeeded = exitCode == 0;
            return new LocalToolRunResult(
                succeeded,
                exitCode,
                false,
                succeeded
                    ? $"{Path.GetFileName(fullPath)} finished successfully."
                    : $"{Path.GetFileName(fullPath)} exited with code {exitCode}.",
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new LocalToolRunResult(false, null, true, "The run was cancelled.", string.Empty);
        }
        catch (Exception exception)
        {
            return new LocalToolRunResult(false, null, false, $"The tool could not be started: {exception.GetType().Name}.", string.Empty);
        }
    }

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

    public async Task<LocalToolRunResult> RunAsync(LocalToolRunRequest request, CancellationToken cancellationToken = default)
    {
        return await RunAsync(request.ExecutablePath, request.Arguments, request.WorkingDirectory, targetPath: null, cancellationToken, request.StandardInput, request.ShowWindow).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the selected executable with optional target path forwarded as its first argument.
    /// The target path is quoted and appended after any user-supplied arguments.
    /// </summary>
    public async Task<LocalToolRunResult> RunAsync(
        string executablePath,
        string arguments,
        string workingDirectory,
        string? targetPath,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        bool showWindow = false)
    {
        ArgumentNullException.ThrowIfNull(executablePath);

        if (string.IsNullOrWhiteSpace(executablePath))
            return new LocalToolRunResult(false, null, false, "No executable was selected.", string.Empty);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LocalToolRunResult(false, null, false, "The selected path is not valid.", string.Empty);
        }

        if (!File.Exists(fullPath))
            return new LocalToolRunResult(false, null, false, "The selected executable was not found.", string.Empty);

        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(fullPath)!
            : workingDirectory;

        if (!Directory.Exists(effectiveWorkingDirectory))
            effectiveWorkingDirectory = Path.GetDirectoryName(fullPath)!;

        var argsBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(arguments))
            argsBuilder.Append(arguments.Trim());

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            if (argsBuilder.Length > 0) argsBuilder.Append(' ');
            argsBuilder.Append('"').Append(targetPath).Append('"');
        }

        // An interactive console tool needs a real terminal, not a pipe: tools built on crates
        // like dialoguer read stdin through `console`, which reports "not a terminal" and makes
        // the process panic before it reads a single byte. ConPTY gives it a terminal we can type
        // into, which is the only way to script such a tool on Windows.
        if (!string.IsNullOrWhiteSpace(standardInput) && OperatingSystem.IsWindows())
        {
            return await ConPtyToolRunner
                .RunAsync(fullPath, argsBuilder.ToString(), effectiveWorkingDirectory, standardInput, cancellationToken)
                .ConfigureAwait(false);
        }

        if (showWindow)
        {
            return await RunWithVisibleWindowAsync(fullPath, argsBuilder.ToString(), effectiveWorkingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = argsBuilder.ToString(),
            WorkingDirectory = effectiveWorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return new LocalToolRunResult(false, null, false, "The executable could not be started.", string.Empty);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Reading has to start before writing: an interactive tool can print its prompt
            // and block on stdin in either order, and a full pipe buffer would deadlock us.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A tool that exits before reading its input closes the pipe first.
                    // That is a normal outcome, not a reason to report a failure.
                }

                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // The writer is already gone; nothing left to close.
                }
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : (int?)null;
            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}{Environment.NewLine}{stderr}".Trim();

            if (cancellationToken.IsCancellationRequested)
                return new LocalToolRunResult(false, exitCode, true, "The run was cancelled.", output);

            var succeeded = exitCode == 0;
            return new LocalToolRunResult(
                succeeded,
                exitCode,
                false,
                succeeded
                    ? $"{Path.GetFileName(fullPath)} finished successfully."
                    : $"{Path.GetFileName(fullPath)} exited with code {exitCode}.",
                output);
        }
        catch (OperationCanceledException)
        {
            return new LocalToolRunResult(false, null, true, "The run was cancelled.", string.Empty);
        }
        catch (Exception exception)
        {
            return new LocalToolRunResult(false, null, false, $"The tool could not be started: {exception.GetType().Name}.", string.Empty);
        }
    }
}
