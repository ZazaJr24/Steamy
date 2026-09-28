using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DepotDumperPage : Page
{
    public DepotDumperPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DepotDumperViewModel>();
    }

    private DepotDumperViewModel ViewModel => (DepotDumperViewModel)DataContext;

    private void GameRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is InstalledGameEntry game)
            ViewModel.SelectedGame = game;
    }

    private void BrowseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should the dumps go?"
        };

        if (dialog.ShowDialog() == true)
            ViewModel.TargetFolder = dialog.FolderName;
    }
}
