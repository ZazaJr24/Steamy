using System.Diagnostics;
using System.Text;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The application must be usable without downloading anything, so Steamless is shipped inside it.
/// These tests protect that guarantee: the files have to be present in the build output and the
/// bundled command line build has to actually start and find its unpackers.
/// </summary>
public sealed class SteamlessBundleTests
{
    [Fact]
    public void TheBundledBuildShipsWithTheApplication()
    {
        Assert.True(
            SteamlessBundle.IsAvailable,
            $"The bundled Steamless build is missing. Expected {SteamlessBundle.CliPath} plus its {SteamlessBundle.PluginsFolderName} folder next to the application.");

        Assert.True(File.Exists(SteamlessBundle.CliPath));
        Assert.True(Directory.Exists(SteamlessBundle.PluginsDirectoryPath));
    }

    [Fact]
    public void EverySteamStubVariantHasItsUnpacker()
    {
        // Without these plugin files Steamless loads nothing and every run fails.
        var required = new[]
        {
            "Steamless.API.dll",
            "SharpDisasm.dll",
            "Steamless.Unpacker.Variant10.x86.dll",
            "Steamless.Unpacker.Variant20.x86.dll",
            "Steamless.Unpacker.Variant21.x86.dll",
            "Steamless.Unpacker.Variant30.x64.dll",
            "Steamless.Unpacker.Variant30.x86.dll",
            "Steamless.Unpacker.Variant31.x64.dll",
            "Steamless.Unpacker.Variant31.x86.dll"
        };

        var missing = required
            .Where(name => !File.Exists(Path.Combine(SteamlessBundle.PluginsDirectoryPath, name)))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing unpacker plugins: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TheLicenseAndAttributionTravelWithTheBinaries()
    {
        // Steamless is third-party software under CC BY-NC-ND 4.0: the author must be named.
        var license = Path.Combine(SteamlessBundle.DirectoryPath, "LICENSE.txt");
        var notice = Path.Combine(SteamlessBundle.DirectoryPath, "NOTICE.md");

        Assert.True(File.Exists(license), "The bundled license file is missing.");
        Assert.True(File.Exists(notice), "The bundled attribution notice is missing.");
        Assert.Contains("atom0s", File.ReadAllText(notice), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts the bundled build for real. This is the end-to-end proof that nothing has to be
    /// downloaded or installed: the process starts and answers without its own window.
    /// </summary>
    [Fact]
    public async Task TheBundledBuildStartsWithoutAnyDownload()
    {
        var (exitCode, output) = await RunBundledAsync(Path.GetTempPath(), null);

        Assert.Contains("Steamless", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usage", output, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(exitCode);
    }

    [Fact]
    public async Task TheBundledBuildRefusesAFileThatIsNotProtected()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"scm-steamless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);

        try
        {
            // Something that is definitely not Steam-wrapped, but is the right shape of input.
            var target = Path.Combine(sandbox, "notpacked.exe");
            await File.WriteAllTextAsync(target, "MZ this is not a protected executable");

            var (_, output) = await RunBundledAsync(sandbox, target);

            Assert.Contains("failed", output, StringComparison.OrdinalIgnoreCase);

            // The unpackers have to be found for that honesty: they live in the Plugins folder.
            Assert.Contains("Loaded plugin", output, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                File.Exists(SteamlessFileSwap.UnpackedPathFor(target)),
                "A file that is not protected must not produce an unpacked result.");
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Runs the bundled CLI exactly like the application does: no window, output captured. The
    /// application's own timeout guards the test, so a hung tool fails instead of blocking.
    /// </summary>
    private static async Task<(int? ExitCode, string Output)> RunBundledAsync(string workingDirectory, string? targetFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = SteamlessBundle.CliPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (targetFile is not null) startInfo.ArgumentList.Add(targetFile);

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, $"{await stdout}{Environment.NewLine}{await stderr}");
    }
}
