using System.IO;
using System.Text.Json;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.4.6", "0.4.6")]
    [InlineData("0.4.6.0", "0.4.6")]
    [InlineData("0.4.6.1", "0.4.6.1")]
    [InlineData("0.4.6.2", "0.4.6.2")]
    public void NormalizationKeepsHotfixesAndTreatsZeroRevisionAsTheBaseRelease(string version, string expected)
        => Assert.Equal(Version.Parse(expected), GitHubUpdateService.Normalize(Version.Parse(version)));

    [Fact]
    public void NewHotfixIsNewerThanItsBaseReleaseAndCannotInstallTheSourceArchive()
    {
        using var release = Release("v0.4.6.1");
        var update = GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,6));
        Assert.NotNull(update);
        Assert.Equal(new Version(0,4,6,1), update.Version);
        Assert.Equal("Steamy-latest.zip", Path.GetFileName(update.AssetUrl.AbsolutePath));
    }

    [Theory]
    [InlineData("v0.4.6")]
    [InlineData("v0.4.6.1")]
    public void InstalledHotfixDoesNotOfferTheSameOrOlderVersion(string tag)
    {
        using var release = Release(tag);
        Assert.Null(GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,6,1)));
    }

    private static JsonDocument Release(string tag) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        assets = new[]
        {
            new { name = $"Steamy-{tag}-source.zip", browser_download_url = $"https://example.invalid/Steamy-{tag}-source.zip", size = 200L },
            new { name = "Steamy-latest.zip", browser_download_url = "https://example.invalid/Steamy-latest.zip", size = 100L }
        }
    }));
}
