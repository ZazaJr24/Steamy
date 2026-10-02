using System.IO;
using System.ComponentModel;
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
        UpdateSetupVisibility();
        Loaded += (_, _) => { Model.RefreshDetection(); UpdateSetupVisibility(); Model.PropertyChanged += Model_PropertyChanged; };
        Unloaded += (_, _) => Model.PropertyChanged -= Model_PropertyChanged;
    }
    private void SteamRoot_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args) => Model.RefreshDetection();
    private void UpdateSetupVisibility()
    {
        if (!string.IsNullOrWhiteSpace(Model.GameInput)) GameExpander.IsExpanded = true;
        if (!Model.BackendInstalled) SteamSetupExpander.IsExpanded = true;
    }
    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(BetterSteamToolsViewModel.GameInput) or nameof(BetterSteamToolsViewModel.BackendInstalled))
            UpdateSetupVisibility();
    }
    private static bool Supports(DragEventArgs args) => args.Data.GetDataPresent(DataFormats.FileDrop)
        && args.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
        && paths.All(path => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar" or ".lua" or ".manifest");
    private void Metadata_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = !Model.IsBusy && Model.SteamDetected && Supports(args) ? DragDropEffects.Copy : DragDropEffects.None;
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
