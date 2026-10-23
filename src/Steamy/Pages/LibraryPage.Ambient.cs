using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;
using Steamy.Models;
using Steamy.ViewModels;
using Steamy.Services;

namespace Steamy.Pages;

public partial class LibraryPage
{
    private Window? _libraryAmbientWindow;
    private bool _libraryAmbientAttached, _libraryHadWorkspaceBrush;
    private object? _libraryPreviousWorkspaceBrush;
    private readonly List<SteamCatalogItem> _libraryAmbientItems = new();
    private readonly DispatcherTimer _libraryAmbientTimer = new(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(32) };
    private Brush? _libraryBackground;
    private bool _libraryBackgroundLight;
    private ImageSource?[] _libraryBackgroundImages = [];
    private CancellationTokenSource? _libraryAmbientRender;
    private static readonly Brush LibraryCharcoal = FrozenBrush(0, 0, 0);

    private void AttachLibraryAmbient()
    {
        if (_libraryAmbientAttached) return;
        _libraryAmbientAttached = true;
        _libraryAmbientWindow = Window.GetWindow(this);
        if (_libraryAmbientWindow is not null)
        {
            _libraryHadWorkspaceBrush = _libraryAmbientWindow.Resources.Contains("WorkspaceBackgroundBrush");
            _libraryPreviousWorkspaceBrush = _libraryAmbientWindow.Resources["WorkspaceBackgroundBrush"];
        }
        UiThemeService.PaletteChanged += LibraryAmbient_ThemeChanged;
        _libraryAmbientTimer.Tick += LibraryAmbient_Tick;
        WatchLibraryAmbientArtwork();
    }

    private void DetachLibraryAmbient()
    {
        CancelLibraryAmbient();
        _libraryAmbientAttached = false;
        UiThemeService.PaletteChanged -= LibraryAmbient_ThemeChanged;
        _libraryAmbientTimer.Stop(); _libraryAmbientTimer.Tick -= LibraryAmbient_Tick;
        foreach(var item in _libraryAmbientItems) item.PropertyChanged -= LibraryAmbient_ArtworkChanged;
        _libraryAmbientItems.Clear();
        if (_libraryAmbientWindow is not null && ReferenceEquals(_libraryAmbientWindow.Resources["WorkspaceBackgroundBrush"], _libraryBackground))
        {
            if (_libraryHadWorkspaceBrush) _libraryAmbientWindow.Resources["WorkspaceBackgroundBrush"] = _libraryPreviousWorkspaceBrush;
            else _libraryAmbientWindow.Resources.Remove("WorkspaceBackgroundBrush");
        }
        _libraryAmbientWindow = null;
        _libraryPreviousWorkspaceBrush = null;
        _libraryBackground = null; _libraryBackgroundImages = [];
    }

    private void LibraryAmbient_ThemeChanged(object? sender, EventArgs e) => WatchLibraryAmbientArtwork();

    private void WatchLibraryAmbientArtwork()
    {
        if(!_libraryAmbientAttached) return;
        foreach(var item in _libraryAmbientItems) item.PropertyChanged -= LibraryAmbient_ArtworkChanged;
        _libraryAmbientItems.Clear();
        if (UiThemeService.IsLight && UiThemeService.AmbientEnabled)
            _libraryAmbientItems.AddRange(((LibraryViewModel)DataContext).PagedCatalogItems.Take(6));
        foreach(var item in _libraryAmbientItems) item.PropertyChanged += LibraryAmbient_ArtworkChanged;
        UpdateLibraryAmbient();
    }
    private void LibraryAmbient_ArtworkChanged(object? sender,PropertyChangedEventArgs args)
    {
        if (!UiThemeService.AmbientEnabled || UiThemeService.IsHighContrastActive) return;
        if(args.PropertyName is not (nameof(SteamCatalogItem.ArtworkImage) or nameof(SteamCatalogItem.HeaderImage))) return;
        if (!_libraryAmbientTimer.IsEnabled) _libraryAmbientTimer.Start();
    }
    private void LibraryAmbient_Tick(object? sender,EventArgs args)
    { _libraryAmbientTimer.Stop(); UpdateLibraryAmbient(); }

    internal static async Task<Brush> CreateLibraryAmbientAsync(ImageSource?[] images, bool light, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!light) return LibraryCharcoal;
        // Only the first and last available artwork contribute to this scene.
        // Do not render the four unused middle palettes on a six-column gallery.
        var first = Array.Find(images, image => image is not null);
        var last = Array.FindLast(images, image => image is not null);
        var left = await ArtworkPalette.FromImageAsync(first, token).ConfigureAwait(false);
        var right = ReferenceEquals(first,last) ? left : await ArtworkPalette.FromImageAsync(last,token).ConfigureAwait(false);
        var palette = new ArtworkPalette(left.Left,right.Right);
        return PaperAmbient.Create(palette);
    }

    private void CancelLibraryAmbient()
    { _libraryAmbientRender?.Cancel(); _libraryAmbientRender?.Dispose(); _libraryAmbientRender = null; }

    private async void UpdateLibraryAmbient()
    {
        if (!_libraryAmbientAttached) return;
        CancelLibraryAmbient();
        var highContrast = UiThemeService.IsHighContrastActive;
        var light = UiThemeService.IsLight;
        var images = light && !highContrast && UiThemeService.AmbientEnabled
            ? _libraryAmbientItems.Select(item => item.ArtworkImage ?? item.HeaderImage).ToArray() : [];
        var cached = _libraryBackgroundLight == light && _libraryBackground is DrawingBrush && images.SequenceEqual(_libraryBackgroundImages);
        Brush background;
        if (highContrast) background = SystemColors.WindowBrush;
        else if (!UiThemeService.AmbientEnabled) background = UiThemeService.BackgroundBrush;
        else if (cached) background = _libraryBackground!;
        else background = light ? Brushes.White : LibraryCharcoal;
        _libraryBackground = background; _libraryBackgroundLight = light; _libraryBackgroundImages = images;
        // Match the shell's window coordinates instead of stretching the scene again per page.
        Background = LibraryScene.Background = Brushes.Transparent;
        LibraryAmbientBackdrop.BaseBrush = background;
        BrushResources.Set(Resources, "PageBackgroundBrush", background);
        BrushResources.Set(Resources, "LibraryFilterBackgroundBrush", highContrast ? background : light ? Brushes.White : Brushes.Black);
        BrushResources.Set(Resources, "TextPrimaryBrush", highContrast ? SystemColors.WindowTextBrush : light ? Brushes.Black : Brushes.White);
        BrushResources.Set(Resources, "LibrarySelectedChipBrush", highContrast ? SystemColors.HighlightBrush : light ? FrozenBrush(229, 237, 255) : FrozenBrush(52, 52, 55));
        BrushResources.Set(Resources, "LibrarySelectedChipBorderBrush", highContrast ? SystemColors.HighlightTextBrush : light ? FrozenBrush(158, 183, 230) : FrozenBrush(104, 104, 109));
        BrushResources.Set(Resources, "LibrarySelectedChipForegroundBrush", highContrast ? SystemColors.HighlightTextBrush : light ? FrozenBrush(23, 57, 120) : Brushes.White);
        if (highContrast) { BrushResources.Set(Resources, "TextSecondaryBrush", SystemColors.WindowTextBrush); BrushResources.Set(Resources, "TextTertiaryBrush", SystemColors.WindowTextBrush); }
        else { BrushResources.SetColor(Resources, "TextSecondaryBrush", light ? Color.FromRgb(55,65,81) : Color.FromRgb(0xC4,0xC4,0xC4)); BrushResources.SetColor(Resources, "TextTertiaryBrush", light ? Color.FromRgb(87,97,113) : Color.FromRgb(0x99,0x99,0x99)); }
        foreach (var key in new[] { "SurfaceBrush", "SurfaceElevatedBrush", "SurfaceSubtleBrush", "GlassSurfaceBrush", "DialogSurfaceBrush", "CardHoverGlassBrush" })
            if (highContrast) BrushResources.Set(Resources, key, SystemColors.WindowBrush); else BrushResources.SetColor(Resources, key, light ? Colors.White : Color.FromRgb(0x09,0x09,0x0B));
        foreach (var key in new[] { "BorderSubtleBrush", "GlassBorderBrush" })
            if (highContrast) BrushResources.Set(Resources, key, SystemColors.WindowTextBrush); else BrushResources.SetColor(Resources, key, light ? Color.FromRgb(213,219,229) : Color.FromRgb(0x30,0x30,0x30));
        if (_libraryAmbientWindow is not null) BrushResources.Set(_libraryAmbientWindow.Resources, "WorkspaceBackgroundBrush", background);
        if (!light || highContrast || !UiThemeService.AmbientEnabled || cached) return;
        var request = _libraryAmbientRender = new CancellationTokenSource();
        var token = request.Token;
        try
        {
            background = await CreateLibraryAmbientAsync(images,light,token);
            if (!ReferenceEquals(_libraryAmbientRender,request) || token.IsCancellationRequested || !_libraryAmbientAttached) return;
            _libraryBackground = background;
            LibraryAmbientBackdrop.BaseBrush = background;
            BrushResources.Set(Resources,"PageBackgroundBrush",background);
            if (_libraryAmbientWindow is not null) BrushResources.Set(_libraryAmbientWindow.Resources,"WorkspaceBackgroundBrush",background);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
        finally { if (ReferenceEquals(_libraryAmbientRender,request)) { _libraryAmbientRender = null; request.Dispose(); } }
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r,g,b));
        brush.Freeze();
        return brush;
    }
}
