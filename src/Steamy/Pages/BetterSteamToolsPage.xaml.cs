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
    private void ImportWorkspace_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Keep the main action and the full drop target beside each other on wide windows.
        // Narrow windows retain natural vertical scrolling, without shrinking input controls.
        var wide = args.NewSize.Width >= 740;
        GameColumn.Width = new GridLength(wide ? 1.1 : 1, GridUnitType.Star);
        DropColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(MetadataDropZone, wide ? 1 : 0);
        Grid.SetRow(MetadataDropZone, wide ? 0 : 1);
        MetadataDropZone.Margin = wide ? new Thickness(18, 0, 0, 0) : new Thickness(0, 16, 0, 0);
    }
    private static bool Supports(DragEventArgs args) => args.Data.GetDataPresent(DataFormats.FileDrop)
        && args.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
        && paths.All(path => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".lua" or ".manifest");
    private void Metadata_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = !Model.IsBusy && Model.BackendInstalled && Supports(args) ? DragDropEffects.Copy : DragDropEffects.None;
        MetadataDropZone.Tag = args.Effects == DragDropEffects.Copy ? "AcceptedDrop" : null;
        args.Handled = true;
    }
    private void Metadata_DragLeave(object sender, DragEventArgs args) => MetadataDropZone.Tag = null;
    private async void Metadata_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        MetadataDropZone.Tag = null;
        if (Supports(args) && args.Data.GetData(DataFormats.FileDrop) is string[] paths) await Model.ImportFilesAsync(paths);
    }
}
