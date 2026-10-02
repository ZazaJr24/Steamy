using Steamy.Models;
using Steamy.Services;
namespace Steamy.UiTests;
public sealed class DownloadMetricsTests
{
    [Fact]
    public void ReservedFilesAndTimeCannotAdvanceLegacyProgress()
    {
        double clock = 0; long writes = 0;
        var tracker = new DownloadProgressTracker(() => writes, () => clock);
        tracker.ObserveLine("Downloading depot 481"); writes = 100L << 30; clock = 10;
        var value = tracker.Snapshot();
        Assert.Null(value.Percent); Assert.Equal(0, value.DownloadedBytes); Assert.Equal(0,value.BytesPerSecond);
        tracker.ObserveLine(" 12.00% file.bin"); var percent = tracker.Snapshot().Percent;
        writes *= 2; clock = 100;
        Assert.Equal(percent, tracker.Snapshot().Percent);
    }
    [Fact]
    public void UnequalUnknownDepotsNeverGetEqualProgressWeights()
    {
        var tracker = new DownloadProgressTracker(() => null);
        tracker.ObserveLine("Processing depot 481"); tracker.ObserveLine("Processing depot 482");
        tracker.ObserveLine("Downloading depot 481"); tracker.ObserveLine("99.00% small.bin");
        Assert.Null(tracker.Snapshot().Percent);
    }
    [Fact]
    public void StructuredProgressUsesValidatedChunksAndCannotCompleteBeforeProcessExit()
    {
        double clock = 0;
        var tracker = new DownloadProgressTracker(() => long.MaxValue, () => clock);
        tracker.ObserveLine("STEAMY_PROGRESS|1|481|checking|400|1000|0|-1|400");
        Assert.Null(tracker.Snapshot().Percent);
        clock = 3; tracker.ObserveLine("STEAMY_PROGRESS|1|481|downloading|800|1000|200|300|400");
        var value = tracker.Snapshot(); Assert.Equal(80, value.Percent); Assert.Equal(200,value.DownloadedBytes);
        Assert.True(value.BytesPerSecond > 0); Assert.NotNull(value.EtaSeconds);
        clock = 4; tracker.ObserveLine("STEAMY_PROGRESS|1|481|downloading|1000|1000|300|300|400");
        Assert.Equal(99.9, tracker.Snapshot().Percent);
        clock = 20; Assert.Equal(0,tracker.Snapshot().BytesPerSecond); Assert.Null(tracker.Snapshot().EtaSeconds);
    }
    [Fact]
    public void KnownDepotsUseActualContentWeights()
    {
        var tracker = new DownloadProgressTracker(() => null);
        tracker.ObserveLine("Processing depot 481"); tracker.ObserveLine("Processing depot 482");
        tracker.ObserveLine("STEAMY_PROGRESS|1|481|checking|0|100|0|-1|0");
        tracker.ObserveLine("STEAMY_PROGRESS|1|482|checking|0|900|0|-1|0");
        tracker.ObserveLine("STEAMY_PROGRESS|1|481|downloading|100|100|50|50|0");
        var value = tracker.Snapshot(); Assert.Equal(10,value.Percent); Assert.Equal(1000,value.TotalBytes);
        Assert.Null(value.TransferTotalBytes); Assert.Null(value.EtaSeconds);
    }
    [Fact]
    public void PausingFreezesElapsedAndClearsLiveEstimates()
    {
        var job = new DownloadJob { State = DownloadJobState.Downloading, Progress = 50, Eta = "~2m", Speed = "1 MiB/s" };
        job.State = DownloadJobState.Paused; job.ClearLiveStats();
        Assert.Equal("Paused",job.RemainingDisplay); Assert.Equal("Paused",job.TransferSpeedDisplay);
        var elapsed = job.ElapsedDisplay; Assert.Equal(elapsed,job.ElapsedDisplay);
        job.State = DownloadJobState.Downloading; job.Progress = double.NaN;
        Assert.True(job.IsProgressIndeterminate); Assert.Equal(0,job.Progress);
        job.Progress = 100; Assert.Equal(99.9,job.Progress);
        job.State = DownloadJobState.Completed; Assert.Equal(100,job.Progress);
    }
    [Theory]
    [InlineData(1073741824L, "1.00 GiB")]
    [InlineData(1048576L, "1.0 MiB")]
    public void BinaryUnitsAreCorrectlyLabelled(long bytes, string expected) => Assert.Equal(expected, DownloadFormat.Bytes(bytes));
}
