using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddToSteamIsAvailableBesidePlayForEveryInstalledGame(bool local)
    {
        var model = Model(new RelatedData(), new Preferences());
        SteamCatalogItem? request = null;
        model.AddToSteamRequested += item => request = item;
        model.SelectGame(Game(284160, local));
        Assert.True(model.CanAddToSteam); Assert.True(model.AddToSteamCommand.CanExecute(null));
        model.AddToSteamCommand.Execute(null);
        Assert.NotNull(request); Assert.Equal(284160, request.AppId); Assert.Equal(model.Game!.Name, request.Name);
        model.SelectGame(new Game { AppId = 284160, IsLocalDownload = local, InstallState = GameInstallState.NotInstalled });
        Assert.False(model.CanAddToSteam); Assert.False(model.AddToSteamCommand.CanExecute(null));
        await model.DeactivateAsync();
    }
}
