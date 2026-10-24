using Steamy.Services;

namespace Steamy.Tests;

public sealed class GameClassificationTests
{
    [Theory]
    [InlineData("Steam Linux Runtime 2.0 (soldier)", true)]
    [InlineData("Steam Linux Runtime 3.0 (sniper)", true)]
    [InlineData("Proton 9.0", true)]
    [InlineData("Proton Experimental", true)]
    [InlineData("Steamworks Common Redistributables", true)]
    [InlineData("Proton Pulse", false)]
    [InlineData("Among Us", false)]
    [InlineData("Content Warning", false)]
    public void DistinguishesSupportPackagesFromGames(string name, bool expected)
        => Assert.Equal(expected, GameClassification.IsRuntime(42, name));

    [Theory]
    [InlineData("NSFW ~ Not a Simulator For Working", true)]
    [InlineData("HENTAI MATCH FANTASY STORIES - ARTWORK - 2 - NSFW", true)]
    [InlineData("Shelter 69 🔞[ADULT NSFW]", true)]
    [InlineData("3001: A MILF Odyssey - NSFW Sci-Fi Porn", true)]
    [InlineData("Pocket Waifu Rekindled 🔞[ADULT NSFW]", true)]
    [InlineData("Call of Duty: Modern Warfare", false)]
    [InlineData("Grand Theft Auto V", false)]
    [InlineData("Cyberpunk 2077", false)]
    public void DistinguishesNsfwGames(string name, bool expected)
        => Assert.Equal(expected, GameClassification.IsNsfw(name));
}
