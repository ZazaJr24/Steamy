using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordMediaTests
{
    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/photo.png?ex=123", true)]
    [InlineData("https://media.discordapp.net/attachments/1/photo.jpg", true)]
    [InlineData("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/header.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/screenshot.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com.evil.example/screenshot.jpg", false)]
    [InlineData("http://cdn.discordapp.com/photo.png", false)]
    [InlineData("https://cdn.discordapp.com.evil.example/photo.png", false)]
    [InlineData("https://evil.example/photo.png", false)]
    [InlineData("https://cdn.discordapp.com:8443/photo.png", false)]
    [InlineData("https://user@cdn.discordapp.com/photo.png", false)]
    [InlineData("https://cdn.discordapp.com/file.exe", false)]
    public void PreviewOnlyUsesTrustedImageLinks(string url, bool expected)
        => Assert.Equal(expected, ChatMediaPolicy.IsImageUri(new Uri(url)));

    [Fact]
    public void ImageOnlyMessageSuppressesRedundantUrlButPreservesCaptions()
    {
        const string url = "https://cdn.discordapp.com/attachments/1/photo.png";
        var message = new DiscordChatMessage(1, 2, 3, 0, 4, url, DateTimeOffset.Now, false, false, false);
        Assert.Equal(url, message.ImageUrl); Assert.Empty(message.DisplayContent);
        Assert.Equal("Look! " + url, (message with { Content = "Look! " + url }).DisplayContent);
        Assert.Equal(url, ChatMediaPolicy.ImageUrl("Look! " + url + ")"));
    }
}
