using Steamy.Services;
namespace Steamy.Tests;
public sealed class DownloadTelemetryTests
{
    [Fact]
    public void TransferContentAndReuseHaveSeparateCounters()
    {
        var value = Assert.IsType<DownloadTelemetry>(DownloadTelemetry.Parse("STEAMY_PROGRESS|1|481|downloading|800|1000|200|300|400"));
        Assert.Equal(800, value.ContentBytes); Assert.Equal(200, value.TransferredBytes);
        Assert.Equal(300, value.TransferTotalBytes); Assert.Equal(400, value.ReusedBytes);
    }
    [Fact]
    public void VerificationDoesNotInventATransferTotal()
    {
        var value = Assert.IsType<DownloadTelemetry>(DownloadTelemetry.Parse("STEAMY_PROGRESS|1|481|checking|600|1000|0|-1|600"));
        Assert.Null(value.TransferTotalBytes); Assert.Equal(0, value.TransferredBytes);
    }
    [Theory]
    [InlineData("STEAMY_PROGRESS|2|481|downloading|0|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|0|downloading|0|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|unknown|0|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|2|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|0|1|2|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|0|1|0|-2|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|NaN|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|18446744073709551615|1|0|1|0")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|0|1|0|1|1")]
    [InlineData("STEAMY_PROGRESS|1|481|downloading|0|1|0|1|0|extra")]
    public void MalformedMessagesNeverSupplyStats(string line) => Assert.Null(DownloadTelemetry.Parse(line));
}
