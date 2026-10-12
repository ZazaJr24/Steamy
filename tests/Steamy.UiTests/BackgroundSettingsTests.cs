using System.Text.Json;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class BackgroundSettingsTests
{
    [Fact]
    public void ExistingProfilesKeepAmbientAndCustomPreferencesRoundTrip()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.AmbientEnabled);
        var choice = new AppSettings { AmbientEnabled = false, BackgroundColor = "#23A9FE" };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(choice))!;
        Assert.False(restored.AmbientEnabled);
        Assert.Equal(choice.BackgroundColor, restored.BackgroundColor);
    }
    [Theory]
    [InlineData("#abcdef", true)]
    [InlineData("#3A7", true)]
    [InlineData("FFFFFF", true)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("Transparent", false)]
    public void ColorInputAcceptsRgbAndRejectsInvalidOrTransparentValues(string input, bool valid)
        => Assert.Equal(valid, UiThemeService.TryBackgroundColor(input, out _));
}
