// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using DepotDownloader;

namespace DepotDownloaderMod.Tests;

public sealed class SteamyStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Steamy-fork-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ManifestPathsCannotEscapeOrUseWindowsDeviceNames()
    {
        Directory.CreateDirectory(directory);
        Assert.Throws<SteamyDownloadException>(() => SteamyPaths.Resolve(directory, "../outside"));
        Assert.Throws<SteamyDownloadException>(() => SteamyPaths.Resolve(directory, "CON.txt"));
        Assert.Equal(Path.Combine(directory, "safe", "file.bin"), SteamyPaths.Resolve(directory, "safe/file.bin"));
    }

    [Fact]
    public void AtomicReplacementKeepsPreviousValidCheckpointAndFailedWritesLeaveItAlone()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checkpoint.bin");
        File.WriteAllText(path, "known-good");

        Assert.Throws<IOException>(() => SteamyAtomicFile.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write("partial");
            writer.Flush();
            throw new IOException("simulated interruption");
        }));
        Assert.Equal("known-good", File.ReadAllText(path));

        SteamyAtomicFile.Write(path, stream => stream.Write("new-state"u8));
        Assert.Equal("new-state", File.ReadAllText(path));
        Assert.Equal("known-good", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void TargetLeaseRejectsConcurrentWritersAndCanBeReacquiredAfterRelease()
    {
        Directory.CreateDirectory(directory);
        using (var lease = new SteamyTargetLease(directory))
            Assert.Throws<SteamyDownloadException>(() => new SteamyTargetLease(directory));
        using var reacquired = new SteamyTargetLease(directory);
    }

    [Fact]
    public void InterruptedFileUpdateRestoresThePreviousValidFile()
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "game.bin");
        File.WriteAllText(target, "previous-valid-content");

        ContentDownloader.CreateRollbackBackup(directory, 70, "game.bin", target);
        File.WriteAllText(target, "partial-update");
        ContentDownloader.RecoverInterruptedWrites(directory);

        Assert.Equal("previous-valid-content", File.ReadAllText(target));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
