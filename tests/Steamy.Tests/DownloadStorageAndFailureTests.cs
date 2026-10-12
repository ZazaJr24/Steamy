using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadStorageAndFailureTests
{
    [Theory]
    [InlineData("HTTP 503 service unavailable", true)]
    [InlineData("Connection reset downloading chunk", true)]
    [InlineData("No download or verification activity for 3 minutes", true)]
    [InlineData("Connection timeout; invalid depot key", false)]
    [InlineData("Disk full; failed to download chunk", false)]
    [InlineData("Manifest not found", false)]
    [InlineData("Unknown tool failure", false)]
    [InlineData("", false)]
    public void AutomaticRetryOnlyAcceptsTransientFailures(string output, bool expected) =>
        Assert.Equal(expected, DownloadFailurePolicy.IsTransient(output));

    [Fact]
    public void AFullDriveCannotStartEvenWhenTheSourceDoesNotKnowTheSize()
    {
        var check = DownloadStorageGuard.Evaluate(0);
        Assert.False(check.CanDownload);
        Assert.Contains("Existing download files were kept", check.Message);
    }

    [Fact]
    public void AvailableSpaceMustIncludeRemainingDownloadAndWorkingRoom()
    {
        const long remaining = 1024 * 1024 * 1024;
        Assert.False(DownloadStorageGuard.Evaluate(remaining, remaining).CanDownload);
        Assert.True(DownloadStorageGuard.Evaluate(remaining + DownloadStorageGuard.WorkingSpaceReserveBytes, remaining).CanDownload);
        Assert.False(DownloadStorageGuard.Evaluate(long.MaxValue - 1, long.MaxValue).CanDownload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-folder")]
    [InlineData("../another-folder")]
    public void InvalidTargetsAreRejectedBeforeTheToolIsStarted(string folder) =>
        Assert.False(DownloadStorageGuard.Check(folder).CanDownload);

    [Theory]
    [InlineData("Connection timeout downloading chunk", true)]
    [InlineData("HTTP 503 service unavailable", true)]
    [InlineData("No space left on device", false)]
    [InlineData("Access denied", false)]
    [InlineData("Account logon denied", false)]
    [InlineData("Invalid depot key", false)]
    public void NetworkFailuresCanRetryButRepeatedPermanentFailuresStop(string output, bool expected) =>
        Assert.Equal(expected, DownloadFailurePolicy.CanRetry(output));

    [Fact]
    public void NetworkAndFullDiskFailuresExplainHowToContinue()
    {
        Assert.Contains("retry to continue", DownloadFailurePolicy.Describe(new HttpRequestException("Fixture outage")));
        var diskFull = new IOException("Disk full", unchecked((int)0x80070070));
        Assert.Contains("drive is full", DownloadFailurePolicy.Describe(diskFull));
        Assert.Contains("Existing files were kept", DownloadFailurePolicy.Describe(diskFull));
    }
}
