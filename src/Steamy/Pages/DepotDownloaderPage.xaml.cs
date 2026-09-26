using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DepotDownloaderPage : Page
{
    public DepotDownloaderPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DepotDownloaderViewModel>();
    }

    private DepotDownloaderViewModel ViewModel => (DepotDownloaderViewModel)DataContext;

    private void BrowseTargetFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select DepotDownloader target folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.TargetFolder = dialog.FolderName;
    }
}
