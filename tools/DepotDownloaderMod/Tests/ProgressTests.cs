using DepotDownloader;
namespace DepotDownloaderMod.Tests;
public sealed class ProgressTests
{
    [Fact]
    public void WireCountersRemainExactAndLocaleIndependent()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("de-DE");
            Assert.Equal("STEAMY_PROGRESS|1|481|downloading|1000|2000|300|600|500",
                SteamyProgress.Format(481,"downloading",1000,2000,300,600,500));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
