// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Steamy fork addition, 2026-10-03.
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader;

internal sealed class SteamyDownloadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class SteamyIdentity
{
    public const string Name = "Steamy DepotDownloaderMod";
    public const string Version = "1.0.0";
    public const string UpstreamRevision = "c0f62fb7f020087f36ae76adfc51fde1446af344";
    public static string Info => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        name = Name,
        version = Version,
        upstreamRevision = UpstreamRevision,
        maxDownloadSpeed = true,
        progressTelemetry = true,
        gracefulStop = true,
        boundedRetries = true,
        atomicResume = true,
        safeManifestPaths = true,
        exclusiveTarget = true,
    });
    public static void Failure(string code) => Console.Error.WriteLine($"STEAMY_FAILURE|1|{code}");
}

/// <summary>Finite retries with bounded jitter; every wait responds to cancellation.</summary>
internal sealed class SteamyRetryBudget
{
    private readonly int maximum;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<double> jitter;
    private int attempts;
    public SteamyRetryBudget(int retries, Func<TimeSpan, CancellationToken, Task> delay = null, Func<double> jitter = null)
    {
        if (retries < 0 || retries > 10) throw new ArgumentOutOfRangeException(nameof(retries));
        maximum = retries + 1;
        this.delay = delay ?? Task.Delay;
        this.jitter = jitter ?? Random.Shared.NextDouble;
    }
    public async Task BeforeAttemptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (attempts >= maximum)
            throw new SteamyDownloadException("retry_exhausted", "Download failed: the retry limit was reached. Resume later to retry the saved version.");
        if (attempts > 0)
        {
            var seconds = Math.Min(15, 0.5 * Math.Pow(2, attempts - 1));
            await delay(TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * Math.Clamp(jitter(), 0, 1))), token).ConfigureAwait(false);
        }
        attempts++;
    }
}

internal sealed class SteamyStopSignal : IAsyncDisposable
{
    private readonly CancellationTokenSource download = new();
    private readonly CancellationTokenSource monitor = new();
    private readonly Task watcher;
    public CancellationToken Token => download.Token;
    public void Cancel() => download.Cancel();
    public SteamyStopSignal(string signalFile)
    {
        if (signalFile != null && !Path.IsPathFullyQualified(signalFile))
            throw new SteamyDownloadException("invalid_options", "The stop signal requires an absolute file path.");
        watcher = signalFile == null ? Task.CompletedTask : WatchAsync(signalFile);
    }
    private async Task WatchAsync(string file)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            do
            {
                if (File.Exists(file)) { download.Cancel(); return; }
            } while (await timer.WaitForNextTickAsync(monitor.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        monitor.Cancel();
        await watcher.ConfigureAwait(false);
        monitor.Dispose();
        download.Dispose();
    }
}

internal static class SteamyRuntime
{
    public static SteamyStopSignal Stop { get; set; }
    public static CancellationToken Token => Stop?.Token ?? CancellationToken.None;
}
