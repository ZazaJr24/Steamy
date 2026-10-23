using System.Text;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class CommunityInviteTests
{
    [Fact]
    public void PublicMetadataIsBoundToTheIntendedServerAndBuildsOnlyDiscordCdnIcons()
    {
        var server = CommunityInviteService.Parse(Encoding.UTF8.GetBytes("""
          {"guild":{"id":"1556047592100470977","name":"Steamy","icon":"a_0123456789abcdef"},"approximate_member_count":50,"approximate_presence_count":12}
          """));
        Assert.Equal("Steamy", server.Name); Assert.Equal(50, server.Members); Assert.Equal(12, server.Online);
        Assert.StartsWith("https://cdn.discordapp.com/icons/1556047592100470977/", server.IconUrl);
        Assert.Throws<InvalidOperationException>(() => CommunityInviteService.Parse(Encoding.UTF8.GetBytes("""
          {"guild":{"id":"1","name":"Different server"}}
          """)));
    }
    [Fact]
    public void InvalidIconsAndUnverifiedCountsAreNotDisplayedAsRealCommunityData()
    {
        var server = CommunityInviteService.Parse(Encoding.UTF8.GetBytes("""
          {"guild":{"id":"1556047592100470977","name":"Steamy","icon":"../../x"},"approximate_member_count":-1}
          """));
        Assert.Null(server.IconUrl); Assert.Null(server.Members); Assert.Null(server.Online);
        Assert.DoesNotContain("members", server.Activity);
    }
}
