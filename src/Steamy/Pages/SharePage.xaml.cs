using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class SharePage : Page
{
    public SharePage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ShareViewModel>();
        Loaded += (_, _) => _ = ViewModel.OnNavigatedToAsync();
    }

    private ShareViewModel ViewModel => (ShareViewModel)DataContext;

    /// <summary>The page owns the file dialog; the view model only writes the ZIP.</summary>
    private async void ExportZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the selected games as one ZIP",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = ViewModel.SuggestedExportName,
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            await ViewModel.ExportSelectedAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"The ZIP could not be saved.{Environment.NewLine}{exception.Message}",
                "Steamy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
