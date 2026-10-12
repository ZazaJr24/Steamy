using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DownloadDeckTelemetryTests
{
    [Fact]
    public void TransferHistoryIsBoundedAndRetainedWithoutAnOpenPage()
    {
        var job = new DownloadJob();
        for (var sample = 0; sample < 90; sample++) job.RecordTransferRate(sample, sample);
        Assert.Equal(60, job.GetTransferRateHistory().Length);
        Assert.Equal(30, job.GetTransferRateHistory()[0]);
        Assert.Equal(89, job.SessionPeakBytesPerSecond);
        job.RecordTransferRate(0, 91);
        Assert.Equal(89, job.SessionPeakBytesPerSecond);
        Assert.Equal(0, job.GetTransferRateHistory()[^1]);
    }
    [Fact]
    public void ProcessWritesMeasureDiskRateWithoutAdvancingNetworkProgress()
    {
        double clock = 0; long writes = 100;
        var tracker = new DownloadProgressTracker(() => writes, () => clock);
        clock = 1; writes = 1_000_100;
        var snapshot = tracker.Snapshot();
        Assert.Equal(1_000_000, snapshot.DiskBytesPerSecond);
        Assert.Equal(0, snapshot.DownloadedBytes);
        Assert.Equal(0, snapshot.BytesPerSecond);
        clock = 2;
        Assert.Equal(0, tracker.Snapshot().DiskBytesPerSecond);
        var job = new DownloadJob();
        GameDownloadProgressMessage.TryApply(job, GameDownloadProgressMessage.Format(1,1,1,0,snapshot));
        Assert.Contains("8", job.DeckDiskRate);
        Assert.Equal(0, job.BytesPerSecond);
    }
    [Fact]
    public void NetworkAndFileProgressUseTheirOwnMeasuredTotals()
    {
        var job = new DownloadJob { State = DownloadJobState.Downloading, Phase = "downloading",
            TransferredBytes = 410, TransferTotalBytes = 1000, ContentBytes = 500, InstallationBytes = 10000 };
        Assert.Equal(41, job.DeckTransferProgress);
        Assert.Equal(5, job.DeckContentProgress);
        job.TransferTotalBytes = null;
        Assert.True(job.DeckTransferIndeterminate);
        Assert.Equal(0, job.DeckTransferProgress);
        Assert.Equal(5, job.DeckContentProgress);
        job.Phase = "finalizing";
        Assert.False(job.DeckTransferIndeterminate);
    }

    [Fact]
    public void PeakSurvivesPauseAndRatesAreConvertedFromBytesToBits()
    {
        var job = new DownloadJob { State = DownloadJobState.Downloading, BytesPerSecond = 10_000_000, DiskSpeed = "25 MiB/s" };
        Assert.Equal(10_000_000, job.SessionPeakBytesPerSecond);
        Assert.Contains("80", job.DeckNetworkRate);
        Assert.Contains("209", job.DeckDiskRate);
        job.BytesPerSecond = 1_000_000;
        job.State = DownloadJobState.Paused;
        job.ClearLiveStats();
        Assert.Equal(10_000_000, job.SessionPeakBytesPerSecond);
        Assert.Equal("—", job.DeckNetworkRate);
        Assert.Equal("—", job.DeckDiskRate);
        Assert.Equal(0, new DownloadJob().SessionPeakBytesPerSecond);
    }
}
