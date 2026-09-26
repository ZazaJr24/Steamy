using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class GreenLumaPage : Page
{
    public GreenLumaPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GreenLumaViewModel>();
    }

    private GreenLumaViewModel ViewModel => (GreenLumaViewModel)DataContext;

    private void BrowseSteamPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your Steam installation folder",
            Multiselect = false,
            ValidateNames = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.SteamPath = dialog.FolderName;
    }

    private void AddAppId_Click(object sender, RoutedEventArgs e)
    {
        var text = AppIdInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        ViewModel.AddAppIds(text);
        AppIdInput.Text = string.Empty;
    }

    private void AppIdInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddAppId_Click(sender, e);
        e.Handled = true;
    }

    private void PasteAppIds_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
                ViewModel.AddAppIds(text);
        }
        catch { }
    }
}
