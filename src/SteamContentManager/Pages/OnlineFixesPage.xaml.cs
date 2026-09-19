using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class OnlineFixesPage : Page
{
    public OnlineFixesPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<OnlineFixesViewModel>();
    }

    private OnlineFixesViewModel ViewModel => (OnlineFixesViewModel)DataContext;

    private void BrowseLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select game install folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.InstallLocation = dialog.FolderName;
    }
}
