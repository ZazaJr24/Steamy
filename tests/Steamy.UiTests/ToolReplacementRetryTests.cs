using System.IO;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ToolReplacementRetryTests
{
    [Fact]
    public async Task ATransientRenameLockPublishesTheWholePreparedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-rename-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "prepared"); var destination = Path.Combine(root, "published");
        Directory.CreateDirectory(Path.Combine(source, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(source, "tool.dll"), "complete tool");
        await File.WriteAllTextAsync(Path.Combine(source, "plugins", "plugin.dll"), "complete plugin");
        try
        {
            var attempts = 0;
            await GitHubToolDownloadService.RetryDirectoryMoveAsync(() =>
            {
                if (++attempts < 3) throw new IOException("Temporary sharing violation", unchecked((int)0x80070020));
                Directory.Move(source, destination);
            }, CancellationToken.None);
            Assert.Equal(3, attempts); Assert.False(Directory.Exists(source));
            Assert.Equal("complete tool", await File.ReadAllTextAsync(Path.Combine(destination, "tool.dll")));
            Assert.Equal("complete plugin", await File.ReadAllTextAsync(Path.Combine(destination, "plugins", "plugin.dll")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PersistentAccessDenialFailsAfterABoundedNumberOfAttempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitHubToolDownloadService.RetryDirectoryMoveAsync(() =>
        { attempts++; throw new UnauthorizedAccessException("Persistent permission denial"); }, CancellationToken.None));
        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task CancellationAndUnrelatedFilesystemErrorsDoNotRetryTheMove()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => GitHubToolDownloadService.RetryDirectoryMoveAsync(() =>
        { attempts++; throw new IOException("Data error", unchecked((int)0x80070017)); }, CancellationToken.None));
        Assert.Equal(1, attempts);
        using var cancellation = new CancellationTokenSource(); attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitHubToolDownloadService.RetryDirectoryMoveAsync(() =>
        { attempts++; cancellation.Cancel(); throw new IOException("Temporary lock", unchecked((int)0x80070021)); }, cancellation.Token));
        Assert.Equal(1, attempts);
    }
}
