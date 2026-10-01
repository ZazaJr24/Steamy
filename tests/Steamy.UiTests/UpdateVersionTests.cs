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
    [InlineData("0.4.7.0", "0.4.7")]
    [InlineData("0.4.7.12", "0.4.7.12")]
    public void NormalizationKeepsHotfixesAndTreatsZeroRevisionAsTheBaseRelease(string version, string expected)
        => Assert.Equal(Version.Parse(expected), GitHubUpdateService.Normalize(Version.Parse(version)));

    [Fact]
    public void NewHotfixPrefersItsVersionedAppEvenWhenLegacyAndSourceAssetsComeFirst()
    {
        using var release = Release("v0.4.7.1", legacy: true);
        var update = GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,7));
        Assert.NotNull(update);
        Assert.Equal(new Version(0,4,7,1), update.Version);
        Assert.Equal("Steamy-v0.4.7.1.zip", Path.GetFileName(update.AssetUrl.AbsolutePath));
    }

    [Theory]
    [InlineData("v0.4.6")]
    [InlineData("v0.4.6.1")]
    public void InstalledHotfixDoesNotOfferTheSameOrOlderVersion(string tag)
    {
        using var release = Release(tag);
        Assert.Null(GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,6,1)));
    }

    [Theory]
    [InlineData("0.4.6.1", "0.4.7")]
    [InlineData("0.4.7", "0.4.7.1")]
    [InlineData("0.4.7.1", "0.4.7.2")]
    [InlineData("0.4.7.9", "0.4.8")]
    public void VersionedOnlyAssetsSupportRegularAndOptionalHotfixUpgrades(string current, string next)
    {
        using var release = Release("v" + next);
        var update = GitHubUpdateService.ParseRelease(release.RootElement, Version.Parse(current));
        Assert.NotNull(update);
        Assert.Equal(Version.Parse(next), update.Version);
        Assert.Equal("Steamy-v" + next + ".zip", Path.GetFileName(update.AssetUrl.AbsolutePath));
    }

    [Fact]
    public void HistoricalLatestAssetStillWorksWhenNoVersionedAppExists()
    {
        using var release = Release("v0.4.6.1", legacy: true, versioned: false);
        var update = GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,6));
        Assert.NotNull(update);
        Assert.Equal("Steamy-latest.zip", Path.GetFileName(update.AssetUrl.AbsolutePath));
    }

    [Fact]
    public void ZeroRevisionDoesNotCreateAnUpdateForTheSameBaseVersion()
    {
        using var release = Release("v0.4.7.0");
        Assert.Null(GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,7)));
    }

    [Fact]
    public void SourceOnlyReleaseCannotBeInstalled()
    {
        using var release = Release("v0.4.7.1", versioned: false);
        Assert.Null(GitHubUpdateService.ParseRelease(release.RootElement, new Version(0,4,7)));
    }

    private static JsonDocument Release(string tag, bool legacy = false, bool versioned = true)
    {
        var names = new List<string> { $"Steamy-{tag}-source.zip" };
        if (legacy) names.Add("Steamy-latest.zip");
        if (versioned) names.Add($"Steamy-{tag}.zip");
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease = false,
            assets = names.Select(name => new { name, browser_download_url = "https://example.invalid/" + name, size = 100L })
        }));
    }
}
