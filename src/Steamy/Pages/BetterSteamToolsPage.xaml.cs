using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class BetterSteamToolsPage : Page
{
    public BetterSteamToolsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<BetterSteamToolsViewModel>();
    }

    private BetterSteamToolsViewModel ViewModel => (BetterSteamToolsViewModel)DataContext;

    private void GameRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is BstGameEntry game)
        {
            ViewModel.SelectedGame = game;
            NoGamePlaceholder.Visibility = Visibility.Collapsed;
            GameDetailsPanel.Visibility = Visibility.Visible;
            GameNameText.Text = game.Name;
            GameAppIdText.Text = game.AppId > 0 ? $"App {game.AppId}" : "Manual entry";
            GamePathText.Text = game.InstallPath;
        }
    }

    private void AddGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select game folder to add",
            Multiselect = false,
            ValidateNames = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.AddGameFolder(dialog.FolderName);
    }

    private void BrowseTool_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select BetterSteamTools executable",
            Filter = "Executable|*.exe|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.SetToolPath(dialog.FileName);
    }
}
