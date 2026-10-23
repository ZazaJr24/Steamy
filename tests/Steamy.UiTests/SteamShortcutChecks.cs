using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckAddToSteam(IServiceProvider provider, MainWindow window, GameDetailsViewModel model)
    {
        var page = Descendants<GameDetailsPage>(window).Single();
        var button = (Button)page.FindName("AddToSteamButton");
        var play = (Button)page.FindName("PlayButton");
        Assert.Same(model.AddToSteamCommand, button.Command);
        foreach (var theme in new[] { "Dark", "Light" })
        foreach (var width in new[] { 980d, 1440d })
        {
            UiThemeService.Apply(theme); window.Width = width; window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
            Assert.Equal(42, button.ActualWidth); Assert.Equal(play.ActualHeight, button.ActualHeight);
            var buttonOrigin = button.TranslatePoint(new Point(), page); var playOrigin = play.TranslatePoint(new Point(), page);
            Assert.InRange(buttonOrigin.X - playOrigin.X - play.ActualWidth, 7, 9);
            Assert.InRange(Math.Abs(buttonOrigin.Y - playOrigin.Y), 0, 1);
            SaveGameDetailsVisual(window, $"add-to-steam-{theme.ToLowerInvariant()}-{width}");
        }
        button.Command.Execute(null); window.UpdateLayout();
        Assert.True(page.IsInstallationOpen);
        var sheet = Assert.IsType<LibraryPage>(page.InstallationSheet);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)sheet.FindName("SourceStepPanel")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)sheet.FindName("MethodStepPanel")).Visibility);
        sheet.SelectDownloadSource(ManifestSource.Local);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)sheet.FindName("LocalPackagePanel")).Visibility);
        Assert.False(((Button)sheet.FindName("StartButton")).IsEnabled);
        sheet.DismissInstallation(); Assert.False(page.IsInstallationOpen);
        window.Width = 1280;
    }
}
