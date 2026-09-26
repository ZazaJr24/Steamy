using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class CreamApiPage : Page
{
    public CreamApiPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<CreamApiViewModel>();
    }

    private CreamApiViewModel ViewModel => (CreamApiViewModel)DataContext;

    private void BrowseGameFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select game folder"
        };

        if (dialog.ShowDialog() == true)
            ViewModel.GameFolder = dialog.FolderName;
    }

    private async void BrowseCreamApiArchive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DLL release archive",
            Filter = "Archives|*.7z;*.zip|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
            await ViewModel.LoadCreamApiDllsAsync(dialog.FileName);
    }

    private void GameRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is InstalledGameEntry game)
            ViewModel.SelectedGame = game;
    }

}
