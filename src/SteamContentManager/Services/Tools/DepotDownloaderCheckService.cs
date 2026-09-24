using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public interface IDepotDownloaderCheckService
{
    Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default);
}

public sealed class DepotDownloaderCheckService : IDepotDownloaderCheckService
{
    public async Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return new DepotDownloaderToolStatus(false, executablePath, string.Empty, "DepotDownloader is not configured.", DateTime.Now);

        if (!File.Exists(executablePath))
            return new DepotDownloaderToolStatus(false, executablePath, string.Empty, "DepotDownloader executable not found.", DateTime.Now);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var match = Regex.Match(output, @"(\d+\.\d+(\.\d+)?)");
            var version = match.Success ? match.Groups[1].Value : "unknown";

            return new DepotDownloaderToolStatus(true, executablePath, version, "DepotDownloader is ready.", DateTime.Now);
        }
        catch (Exception exception)
        {
            return new DepotDownloaderToolStatus(false, executablePath, string.Empty, $"Check failed: {exception.Message}", DateTime.Now);
        }
    }
}
