using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

/// <summary>
/// Shared page shell for the "run my own external tool" workflows. The concrete pages below only
/// supply their view model; the interaction (pick executable, pick working directory, run) is
/// identical, so it stays in one place.
/// </summary>
public partial class ToolRunnerPage : Page
{
    protected ToolRunnerPage(ToolRunnerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    protected ToolRunnerViewModel ViewModel => (ToolRunnerViewModel)DataContext;

    private async void BrowseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await BrowseExecutableAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The selected tool could not be read.{Environment.NewLine}{exception.Message}",
                ViewModel.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task BrowseExecutableAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select executable ({ViewModel.ExpectedFileNameHint})",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        ViewModel.WorkingDirectory = System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        await ViewModel.SetExecutableAsync(dialog.FileName);
    }

    private async void ExecutablePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // A path that was typed or pasted never went through the file dialog, so give it the same
        // treatment: re-check it and remember it. Guarded, because a throwing handler would close
        // the whole application.
        try
        {
            await ViewModel.PathEnteredAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The path could not be checked.{Environment.NewLine}{exception.Message}",
                ViewModel.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BrowseWorkingDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the working directory for this tool",
            Multiselect = false,
            ValidateNames = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.WorkingDirectory = dialog.FolderName;
    }
}

public sealed class XStoreUnlockerPage : ToolRunnerPage
{
    public XStoreUnlockerPage()
        : base(App.Services.GetRequiredService<XStoreUnlockerViewModel>())
    {
    }
}
