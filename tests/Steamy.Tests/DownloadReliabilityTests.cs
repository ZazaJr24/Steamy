using Steamy.Models;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadOperationRegistryTests
{
    [Fact]
    public async Task ConcurrentRegistrationHasExactlyOneOwner()
    {
        var registry = new DownloadOperationRegistry();
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => registry.TryRegister(id, cancellation))));
        Assert.Equal(1, results.Count(registered => registered));
    }

    [Fact]
    public async Task PauseKeepsTheJobReservedUntilProcessAndSaveFinish()
    {
        var registry = new DownloadOperationRegistry();
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        Assert.True(registry.TryRegister(id, cancellation));
        var operation = Assert.IsType<DownloadOperationRegistry.Operation>(registry.Find(id));
        operation.RequestStop(pause: true);
        operation.Cancel();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(registry.IsPauseRequested(id));
        Assert.False(operation.Completion.IsCompleted);
        Assert.False(registry.TryRegister(id, replacement));

        registry.Complete(id);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(registry.IsPauseRequested(id));
        Assert.True(registry.TryRegister(id, replacement));
        Assert.False(replacement.IsCancellationRequested);
    }

    [Fact]
    public void CancellingOneJobDoesNotBlockOrCancelAnother()
    {
        var registry = new DownloadOperationRegistry();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        Assert.True(registry.TryRegister(first, firstCancellation));
        Assert.True(registry.TryRegister(second, secondCancellation));
        registry.Find(first)!.RequestStop(pause: false);
        registry.Find(first)!.Cancel();
        Assert.True(firstCancellation.IsCancellationRequested);
        Assert.False(registry.IsPauseRequested(first));
        Assert.False(secondCancellation.IsCancellationRequested);
        Assert.True(registry.IsRunning(second));
    }

    [Fact]
    public void CancellationRacingWithOwnerDisposalDoesNotThrow()
    {
        var registry = new DownloadOperationRegistry();
        var id = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        Assert.True(registry.TryRegister(id, cancellation));
        cancellation.Dispose();
        registry.CancelAll();
        registry.Complete(id);
        Assert.False(registry.IsRunning(id));
    }
}

public sealed class DownloadReliabilityTests
{
    [Theory]
    [InlineData("DepotDownloader", null, false)]
    [InlineData("depotdownloader", 123, false)]
    [InlineData("DepotDownloaderMod (Ryuu)", null, true)]
    [InlineData("DepotDownloaderMod (Zaza)", 123, true)]
    [InlineData("Hubcap", null, true)]
    [InlineData("", null, true)]
    [InlineData("", 123, false)]
    [InlineData("Not configured", null, false)]
    public void ResumeUsesTheRecordedToolEvenForWholeAppDownloads(string mode, int? depotId, bool expected) =>
        Assert.Equal(expected, DownloadJobPolicy.UsesModDownloader(mode, depotId, "existing-folder"));

    [Theory]
    [InlineData(DownloadJobState.Paused)]
    [InlineData(DownloadJobState.Failed)]
    [InlineData(DownloadJobState.Cancelled)]
    [InlineData(DownloadJobState.Queued)]
    public void FindingFilesNeverCompletesAnUnfinishedJob(DownloadJobState previous)
    {
        Assert.Equal(previous, DownloadJobPolicy.AfterLocalCheck(previous, hasContent: true));
        Assert.Equal(previous, DownloadJobPolicy.AfterLocalCheck(previous, hasContent: false));
    }

    [Fact]
    public void ACompletedJobWithMissingFilesNeedsAttention() =>
        Assert.Equal(DownloadJobState.Failed, DownloadJobPolicy.AfterLocalCheck(DownloadJobState.Completed, hasContent: false));

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Downloading")]
    [InlineData("Verifying")]
    public void RestartNeverRestoresAnInterruptedJobAsRunning(string state)
    {
        Assert.Equal(DownloadJobState.Paused, DownloadJobPolicy.RestoreState(state, autoResume: false));
        Assert.Equal(DownloadJobState.Queued, DownloadJobPolicy.RestoreState(state, autoResume: true));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void CorruptQueueStateIsRestoredPaused(string? state) =>
        Assert.Equal(DownloadJobState.Paused, DownloadJobPolicy.RestoreState(state, autoResume: true));
}

public sealed class LocalDownloadInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Steamy-verification-" + Guid.NewGuid().ToString("N"));

    public LocalDownloadInspectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DownloadCacheAloneIsNotACompletedGame()
    {
        var cache = Directory.CreateDirectory(Path.Combine(_root, ".DepotDownloader"));
        File.WriteAllText(Path.Combine(cache.FullName, "123.manifest"), "manifest metadata");
        var result = LocalDownloadInspector.Inspect(_root);
        Assert.False(result.HasContent);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public void CountsNestedFilesAndEmptyFilesWithoutClaimingIntegrity()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "game", "data"));
        File.WriteAllBytes(Path.Combine(content.FullName, "content.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(_root, "empty.txt"), Array.Empty<byte>());
        var cache = Directory.CreateDirectory(Path.Combine(_root, ".depotdownloader"));
        File.WriteAllBytes(Path.Combine(cache.FullName, "partial.chunk"), new byte[2048]);

        var result = LocalDownloadInspector.Inspect(_root);
        Assert.True(result.HasContent);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(1024, result.TotalBytes);
        Assert.Contains("Integrity is checked by the downloader", result.Message);
    }

    [Fact]
    public void EmptyFilesAloneDoNotProveTheDownloadHasContent()
    {
        File.WriteAllBytes(Path.Combine(_root, "game.bin"), Array.Empty<byte>());
        var result = LocalDownloadInspector.Inspect(_root);
        Assert.False(result.HasContent);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void MissingDirectoryDoesNotSucceed() =>
        Assert.False(LocalDownloadInspector.Inspect(Path.Combine(_root, "missing")).HasContent);

    [Fact]
    public void CancellationIsObservedBeforeInspectingTheFolder()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LocalDownloadInspector.Inspect(_root, cancellation.Token));
    }

    [Fact]
    public void DirectoryLinksCannotCountUnrelatedFilesOrCreateCycles()
    {
        // Creating symlinks on Windows requires a privilege that CI runners need not grant.
        if (OperatingSystem.IsWindows()) return;
        var content = Directory.CreateDirectory(Path.Combine(_root, "content"));
        File.WriteAllBytes(Path.Combine(content.FullName, "game.bin"), new byte[12]);
        Directory.CreateSymbolicLink(Path.Combine(content.FullName, "loop"), _root);
        var result = LocalDownloadInspector.Inspect(_root);
        Assert.True(result.HasContent);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(12, result.TotalBytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
