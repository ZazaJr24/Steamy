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
        UpdateLibraryAmbient();
    }

    private void DetachLibraryAmbient()
    {
        _libraryAmbientAttached = false;
        UiThemeService.PaletteChanged -= LibraryAmbient_ThemeChanged;
        _libraryAmbientTimer.Stop(); _libraryAmbientTimer.Tick -= LibraryAmbient_Tick;
        foreach(var item in _libraryAmbientItems) item.PropertyChanged -= LibraryAmbient_ArtworkChanged;
        _libraryAmbientItems.Clear();
        if (_libraryAmbientWindow is not null)
        {
            if (_libraryHadWorkspaceBrush) _libraryAmbientWindow.Resources["WorkspaceBackgroundBrush"] = _libraryPreviousWorkspaceBrush;
            else _libraryAmbientWindow.Resources.Remove("WorkspaceBackgroundBrush");
        }
        _libraryAmbientWindow = null;
    }

    private void LibraryAmbient_ThemeChanged(object? sender, EventArgs e) => UpdateLibraryAmbient();

    private void WatchLibraryAmbientArtwork()
    {
        if(!_libraryAmbientAttached) return;
        foreach(var item in _libraryAmbientItems) item.PropertyChanged -= LibraryAmbient_ArtworkChanged;
        _libraryAmbientItems.Clear();
        _libraryAmbientItems.AddRange(((LibraryViewModel)DataContext).PagedCatalogItems.Take(6));
        foreach(var item in _libraryAmbientItems) item.PropertyChanged += LibraryAmbient_ArtworkChanged;
        UpdateLibraryAmbient();
    }
    private void LibraryAmbient_ArtworkChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is not (nameof(SteamCatalogItem.ArtworkImage) or nameof(SteamCatalogItem.HeaderImage))) return;
        if (!_libraryAmbientTimer.IsEnabled) _libraryAmbientTimer.Start();
    }
    private void LibraryAmbient_Tick(object? sender,EventArgs args)
    { _libraryAmbientTimer.Stop(); UpdateLibraryAmbient(); }

    private Brush CreateLibraryAmbient()
    {
        var palettes=_libraryAmbientItems.Select(item=>item.ArtworkImage ?? item.HeaderImage).Where(image=>image is not null).Select(ArtworkPalette.FromImage).ToArray();
        var area=new RectangleGeometry(new Rect(0,0,1,1));
        var drawing=new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(FrozenBrush(10,13,17),null,area));
        // A continuous scene wash, sampled from every cover, without a black footer zone.
        var colors = palettes.Length == 0 ? new[] { ArtworkPalette.Neutral.Left, ArtworkPalette.Neutral.Right }
            : palettes.Select(palette => palette.Left).ToArray();
        for (var index = 0; index < colors.Length; index++)
        {
            var color = colors[index];
            var center = new Point((index + .5) / colors.Length, .38);
            var glow=new RadialGradientBrush { Center=center,GradientOrigin=center,RadiusX=.36,RadiusY=1.05 };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(88,color.R,color.G,color.B),0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(34,color.R,color.G,color.B),.65));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),1));
            drawing.Children.Add(new GeometryDrawing(glow,null,area));
            // Carry the same cover colors through the toolbar, with a soft fade into the grid.
            var topCenter = new Point(center.X, .06);
            var topGlow = new RadialGradientBrush { Center=topCenter,GradientOrigin=topCenter,RadiusX=.30,RadiusY=.50 };
            topGlow.GradientStops.Add(new GradientStop(Color.FromArgb(44,color.R,color.G,color.B),0));
            topGlow.GradientStops.Add(new GradientStop(Color.FromArgb(14,color.R,color.G,color.B),.60));
            topGlow.GradientStops.Add(new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),1));
            drawing.Children.Add(new GeometryDrawing(topGlow,null,area));
        }
        // WPF interpolates RGB as well as alpha. Transparent white would form a gray band.
        var fade=new LinearGradientBrush(Color.FromArgb(0,10,13,17),Color.FromArgb(160,10,13,17),new Point(0,.50),new Point(0,1));
        drawing.Children.Add(new GeometryDrawing(fade,null,area));
        var brush=new DrawingBrush(drawing) { Stretch=Stretch.Fill }; brush.Freeze(); return brush;
    }

    private void UpdateLibraryAmbient()
    {
        if (!_libraryAmbientAttached) return;
        var highContrast = UiThemeService.IsHighContrastActive;
        Brush background = highContrast ? SystemColors.WindowBrush : UiThemeService.AmbientEnabled ? CreateLibraryAmbient() : Brushes.Black;
        // Match the shell's window coordinates instead of stretching the scene again per page.
        Background = LibraryScene.Background = Brushes.Transparent;
        LibraryAmbientBackdrop.BaseBrush = background;
        Resources["PageBackgroundBrush"] = background;
        Resources["TextPrimaryBrush"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        Resources["TextSecondaryBrush"] = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(0xC4,0xC4,0xC4);
        Resources["TextTertiaryBrush"] = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(0x99,0x99,0x99);
        foreach (var key in new[] { "SurfaceBrush", "SurfaceElevatedBrush", "SurfaceSubtleBrush", "GlassSurfaceBrush", "DialogSurfaceBrush", "CardHoverGlassBrush" })
            Resources[key] = highContrast ? SystemColors.WindowBrush : FrozenBrush(0x16,0x16,0x16);
        foreach (var key in new[] { "BorderSubtleBrush", "GlassBorderBrush" })
            Resources[key] = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(0x30,0x30,0x30);
        if (_libraryAmbientWindow is not null) _libraryAmbientWindow.Resources["WorkspaceBackgroundBrush"] = background;
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r,g,b));
        brush.Freeze();
        return brush;
    }
}
