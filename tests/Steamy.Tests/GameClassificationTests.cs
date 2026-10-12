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
}
