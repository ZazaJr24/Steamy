using System.Windows;
using System.Windows.Input;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;
using System.ComponentModel;
using System.Windows.Media;

namespace Steamy.Pages;

public partial class DownloadsPage
{
    private Window? _downloadSceneWindow;
    private object? _previousDownloadWorkspace;
    private bool _hadDownloadWorkspace;
    private Brush? _downloadScene;
    private ArtworkPalette? _downloadScenePalette;
    private bool _downloadSceneLight;
    private bool _downloadSceneAttached;
    private CancellationTokenSource? _downloadSceneRender;
    private void AttachDownloadAmbient()
    {
        if (_downloadSceneAttached) return;
        _downloadSceneAttached = true;
        _downloadSceneWindow = Window.GetWindow(this);
        if (_downloadSceneWindow is not null)
        { _hadDownloadWorkspace = _downloadSceneWindow.Resources.Contains("WorkspaceBackgroundBrush"); _previousDownloadWorkspace = _downloadSceneWindow.Resources["WorkspaceBackgroundBrush"]; }
        ((DownloadsViewModel)DataContext).PropertyChanged += DownloadArtworkChanged;
        MotionPreferences.Changed += DownloadPaletteChanged;
        UiThemeService.PaletteChanged += DownloadPaletteChanged;
        ApplyDeckAccessibility();
        UpdateDownloadScene();
    }
    private void DetachDownloadAmbient()
    {
        _downloadSceneAttached = false;
        CancelDownloadScene();
        MotionPreferences.Changed -= DownloadPaletteChanged;
        UiThemeService.PaletteChanged -= DownloadPaletteChanged;
        ((DownloadsViewModel)DataContext).PropertyChanged -= DownloadArtworkChanged;
        if (_downloadSceneWindow is not null && ReferenceEquals(_downloadSceneWindow.Resources["WorkspaceBackgroundBrush"], _downloadScene))
        { if (_hadDownloadWorkspace) _downloadSceneWindow.Resources["WorkspaceBackgroundBrush"] = _previousDownloadWorkspace; else _downloadSceneWindow.Resources.Remove("WorkspaceBackgroundBrush"); }
        _downloadSceneWindow = null; _downloadScene = null;
        _previousDownloadWorkspace = null; _downloadScenePalette = null;
    }
    private void DownloadPaletteChanged(object? sender, EventArgs args) { ApplyDeckAccessibility(); UpdateDownloadScene(); }
    private void DownloadArtworkChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(DownloadsViewModel.HeroArtwork) or nameof(DownloadsViewModel.HeroJob)) UpdateDownloadScene(); }
    private void CancelDownloadScene()
    { _downloadSceneRender?.Cancel(); _downloadSceneRender?.Dispose(); _downloadSceneRender = null; }
    private async void UpdateDownloadScene()
    {
        if (!_downloadSceneAttached) return;
        CancelDownloadScene();
        var accessible = UiThemeService.IsHighContrastActive;
        var ambient = UiThemeService.AmbientEnabled && !accessible;
        var source = ((DownloadsViewModel)DataContext).HeroArtwork;
        var light = UiThemeService.IsLight;
        _downloadScene = accessible ? SystemColors.WindowBrush : !ambient ? UiThemeService.BackgroundBrush
            : !light ? Brushes.Black : _downloadScene is DrawingBrush && _downloadSceneLight == light ? _downloadScene : Brushes.White;
        BrushResources.Set(Resources, "DeckBackground", _downloadScene);
        if (ambient)
        {
            BrushResources.Set(Resources, "DeckHeroBackground", (Brush)FindResource("GlassSurfaceBrush"));
            BrushResources.Set(Resources, "DeckLine", FrozenColor(UiThemeService.IsLight ? "#240F1728" : "#1FFFFFFF"));
        }
        if (_downloadSceneWindow is not null) BrushResources.Set(_downloadSceneWindow.Resources, "WorkspaceBackgroundBrush", _downloadScene);
        if (!ambient || !light) return;
        var request = _downloadSceneRender = new CancellationTokenSource();
        var token = request.Token;
        try
        {
            var palette = await ArtworkPalette.FromImageAsync(source,token);
            if (!ReferenceEquals(_downloadSceneRender,request) || token.IsCancellationRequested || !_downloadSceneAttached) return;
            _downloadScene = _downloadScene is DrawingBrush && _downloadScenePalette == palette && _downloadSceneLight == light
                ? _downloadScene : CreateDownloadScene(palette,light);
            _downloadScenePalette = palette; _downloadSceneLight = light;
            BrushResources.Set(Resources,"DeckBackground",_downloadScene);
            if (_downloadSceneWindow is not null) BrushResources.Set(_downloadSceneWindow.Resources,"WorkspaceBackgroundBrush",_downloadScene);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
        finally { if (ReferenceEquals(_downloadSceneRender,request)) { _downloadSceneRender=null; request.Dispose(); } }
    }
    private static Brush FrozenColor(string value)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
    private static Brush CreateDownloadScene(ArtworkPalette palette, bool light)
    {
        if (!light) return Brushes.Black;
        return PaperAmbient.Create(palette);
    }
    // Hovering a download changes its actions, not the page background or every glass surface.
    private void DownloadAmbient_Enter(object sender, MouseEventArgs args) { }
    private void DownloadAmbient_Leave(object sender, MouseEventArgs args) { }
    private void DownloadAmbient_Focus(object sender, KeyboardFocusChangedEventArgs args) { }
    private void DownloadAmbient_Blur(object sender, KeyboardFocusChangedEventArgs args) { }
    private void DownloadAmbient_TargetChanged(object sender, DependencyPropertyChangedEventArgs args) { }
    private void RefreshDownloadAmbientGeometry() { }
    internal Task PreviewDownloadAmbientAsync(DownloadJob? job) => Task.CompletedTask;
}
