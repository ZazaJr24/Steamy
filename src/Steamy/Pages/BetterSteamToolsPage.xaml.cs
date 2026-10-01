using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class BetterSteamToolsPage : Page
{
    private BetterSteamToolsViewModel Model => (BetterSteamToolsViewModel)DataContext;
    public BetterSteamToolsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<BetterSteamToolsViewModel>();
        Loaded += (_, _) => Model.RefreshDetection();
    }
    private void SteamRoot_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args) => Model.RefreshDetection();
    private static bool Supports(DragEventArgs args) => args.Data.GetDataPresent(DataFormats.FileDrop)
        && args.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
        && paths.All(path => Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".lua" or ".manifest");
    private void Metadata_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = !Model.IsBusy && Model.BackendInstalled && Supports(args) ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }
    private async void Metadata_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (Supports(args) && args.Data.GetData(DataFormats.FileDrop) is string[] paths) await Model.ImportFilesAsync(paths);
    }
}
