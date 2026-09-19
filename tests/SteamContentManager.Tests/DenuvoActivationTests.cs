using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The Steam ticket generator accepts no command line flags at all. It prints "Enter the App ID"
/// and reads the answer from stdin, then waits for two more Enter presses before it exits. Running
/// it with "--appid" left it blocking on a stream nobody answered, so generation silently did
/// nothing. These tests pin the real contract.
/// </summary>
public sealed class DenuvoActivationTests
{
    [Fact]
    public async Task TheAppIdIsWrittenToTheToolInsteadOfPassedAsAFlag()
    {
        var store = new AppDataStore();
        var runner = new RecordingToolRunner();
        var toolPath = CreateTempToolFile();

        var viewModel = new DenuvoActivationViewModel(
            store,
            new Nav(),
            new InMemoryLoggingService(store, new NullLocalDatabase()),
            runner,
            new CachedToolDownloader(toolPath),
            new Settings());

        try
        {
            await viewModel.EnsureToolDownloadedAsync();
            viewModel.ManualAppId = "730";

            Assert.True(viewModel.CanRun);
            await viewModel.RunCommand.ExecuteAsync(null);
        }
        finally
        {
            File.Delete(toolPath);
        }

        var request = Assert.Single(runner.Requests);

        Assert.DoesNotContain("--appid", request.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, request.Arguments);

        var expectedAnswers = "730" + Environment.NewLine + Environment.NewLine + Environment.NewLine;
        Assert.Equal(expectedAnswers, request.StandardInput);

        // steam_api64.dll ships next to the executable, so the tool must start in its own folder.
        Assert.Equal(Path.GetDirectoryName(toolPath), request.WorkingDirectory);
    }

    [Fact]
    public async Task AReadyToolIsReusedInsteadOfDownloadedAgain()
    {
        var store = new AppDataStore();
        var toolPath = CreateTempToolFile();
        var downloader = new CachedToolDownloader(toolPath);

        var viewModel = new DenuvoActivationViewModel(
            store,
            new Nav(),
            new InMemoryLoggingService(store, new NullLocalDatabase()),
            new RecordingToolRunner(),
            downloader,
            new Settings());

        try
        {
            await viewModel.EnsureToolDownloadedAsync();

            Assert.Equal(0, downloader.DownloadCalls);
            Assert.False(viewModel.HasDownloadError);
            Assert.True(viewModel.CanRun is false); // still needs an AppID
        }
        finally
        {
            File.Delete(toolPath);
        }
    }

    [Fact]
    public async Task TheRunnerFeedsScriptedAnswersToAnInteractiveTool()
    {
        // A tiny batch script that blocks on stdin exactly like the ticket generator does.
        var script = Path.Combine(Path.GetTempPath(), $"stdin-probe-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(
            script,
            "@echo off" + Environment.NewLine + "set /p v=" + Environment.NewLine + "echo GOT=%v%" + Environment.NewLine);

        try
        {
            var runner = new LocalToolRunnerService();
            var request = new LocalToolRunRequest(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/c \"{script}\"",
                Path.GetTempPath(),
                "730" + Environment.NewLine);

            var result = await runner.RunAsync(request);

            Assert.True(result.Succeeded, $"{result.Message} | {result.Output}");
            Assert.Contains("GOT=730", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task AnInteractiveToolSeesARealTerminalAndNotAPipe()
    {
        // The ticket generator aborts with "not a terminal" as soon as stdin is a pipe, so the
        // runner has to hand it a real console. PowerShell reports exactly that difference.
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        Assert.True(File.Exists(powershell), $"powershell.exe not found at {powershell}");

        var runner = new LocalToolRunnerService();
        var request = new LocalToolRunRequest(
            powershell,
            "-NoProfile -Command \"Write-Output ('REDIRECTED=' + [Console]::IsInputRedirected)\"",
            Path.GetTempPath(),
            "x" + Environment.NewLine);

        var result = await runner.RunAsync(request);

        Assert.True(result.Succeeded, $"runner failed: {result.Message} | output: {result.Output}");
        Assert.Contains("REDIRECTED=False", result.Output, StringComparison.Ordinal);
    }

    private static string CreateTempToolFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"steam-ticket-generator-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class Nav : INavigationService
    {
        public void Attach(Action<Type> navigate) { }
        public void Detach() { }
        public void Navigate<TPage>() { }
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new();
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CachedToolDownloader : IDenuvoGeneratorDownloadService
    {
        private readonly string _path;

        public CachedToolDownloader(string path) => _path = path;

        public int DownloadCalls { get; private set; }

        public string? CachedPath => File.Exists(_path) ? _path : null;
        public bool HasCachedExecutable => File.Exists(_path);

        public Task<DenuvoGeneratorDownloadResult> DownloadLatestAsync(CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(new DenuvoGeneratorDownloadResult(false, "offline", null, null));
        }

        public void ClearCache() { }
    }

    private sealed class RecordingToolRunner : ILocalToolRunner
    {
        public List<LocalToolRunRequest> Requests { get; } = new();

        public Task<LocalToolStatus> CheckAsync(string executablePath, string expectedFileNameHint, CancellationToken cancellationToken = default)
            => Task.FromResult(new LocalToolStatus(true, executablePath, "ready", null, DateTime.Now));

        public Task<LocalToolRunResult> RunAsync(LocalToolRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            // Deliberately unparseable: the page must not touch the user's Desktop in a test.
            return Task.FromResult(new LocalToolRunResult(false, 0, false, "no ticket", "no ticket here"));
        }
    }
}
