using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class HubPage : Page
{
    public HubPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<HubViewModel>();
    }

    private void GameCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not Game game) return;
        var vm = (HubViewModel)DataContext;
        if (vm.DownloadGameCommand.CanExecute(game))
            vm.DownloadGameCommand.Execute(game);
    }
}
