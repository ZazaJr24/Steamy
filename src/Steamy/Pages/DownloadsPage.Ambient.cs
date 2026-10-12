using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DownloadsPage
{
    private readonly Dictionary<int, Brush> _ambientPalettes = new();
    private readonly Dictionary<int, ImageSource> _ambientImages = new();
    private CancellationTokenSource? _ambientLoad;
    private DownloadJob? _ambientJob;
    private FrameworkElement? _ambientMouseTarget, _ambientFocusTarget;
    private bool _ambientFrontIsA, _ambientAttached, _hadWorkspaceBrush;
    private object? _previousWorkspaceBrush;
    private Window? _ambientWindow;
    private void AttachDownloadAmbient()
    {
        if (_ambientAttached) return;
        _ambientAttached = true;
        _ambientWindow = Window.GetWindow(this);
        if (_ambientWindow is not null)
        {
            _hadWorkspaceBrush = _ambientWindow.Resources.Contains("WorkspaceBackgroundBrush");
            _previousWorkspaceBrush = _ambientWindow.Resources["WorkspaceBackgroundBrush"];
        }
        MotionPreferences.Changed += DownloadAmbient_PreferencesChanged;
        UiThemeService.PaletteChanged += DownloadAmbient_PreferencesChanged;
        ((DownloadsViewModel)DataContext).PropertyChanged += DownloadAmbient_ModelChanged;
        DownloadAmbient_PreferencesChanged(null, EventArgs.Empty);
    }

    private void DetachDownloadAmbient()
    {
        _ambientAttached = false;
        MotionPreferences.Changed -= DownloadAmbient_PreferencesChanged;
        UiThemeService.PaletteChanged -= DownloadAmbient_PreferencesChanged;
        ((DownloadsViewModel)DataContext).PropertyChanged -= DownloadAmbient_ModelChanged;
        _ambientMouseTarget = _ambientFocusTarget = null;
        CancelAmbientLoad(); _ambientJob = null;
        FadeAmbient(DownloadAmbientA, 0, false); FadeAmbient(DownloadAmbientB, 0, false);
        FadeAmbient(DownloadPageAmbientA, 0, false); FadeAmbient(DownloadPageAmbientB, 0, false);
        if (_ambientWindow is not null)
        {
            if (_hadWorkspaceBrush) _ambientWindow.Resources["WorkspaceBackgroundBrush"] = _previousWorkspaceBrush;
            else _ambientWindow.Resources.Remove("WorkspaceBackgroundBrush");
        }
        _ambientWindow = null;
    }

    private void DownloadAmbient_Enter(object sender, MouseEventArgs e)
    { _ambientMouseTarget = (FrameworkElement)sender; RefreshAmbientTarget(); }
    private void DownloadAmbient_Leave(object sender, MouseEventArgs e)
    { if (ReferenceEquals(_ambientMouseTarget, sender)) _ambientMouseTarget = null; RefreshAmbientTarget(); }
    private void DownloadAmbient_TargetChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(_ambientMouseTarget, sender) || ReferenceEquals(_ambientFocusTarget, sender)) RefreshAmbientTarget();
    }
    private void DownloadAmbient_Focus(object sender, KeyboardFocusChangedEventArgs e)
    { _ambientFocusTarget = (FrameworkElement)sender; RefreshAmbientTarget(); }
    private void DownloadAmbient_Blur(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (sender is FrameworkElement { IsKeyboardFocusWithin: false } && ReferenceEquals(_ambientFocusTarget, sender))
                _ambientFocusTarget = null;
            RefreshAmbientTarget();
        });
    }
    private void DownloadAmbient_ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadsViewModel.HeroJob) or nameof(DownloadsViewModel.HeroArtwork) or nameof(DownloadsViewModel.NextQueuedJob))
        {
            if (_ambientJob is { } job)
            {
                if (e.PropertyName == nameof(DownloadsViewModel.HeroArtwork)) { _ambientPalettes.Remove(job.AppId); _ambientImages.Remove(job.AppId); }
                _ambientJob = null;
            }
            RefreshAmbientTarget();
        }
    }
    private void RefreshAmbientTarget()
    {
        if (!_ambientAttached) return;
        var model = (DownloadsViewModel)DataContext;
        _ = PreviewDownloadAmbientAsync(model.HeroJob ?? model.NextQueuedJob);
    }

    // Cancellation and identity checks keep a late image from painting a different download.
    internal async Task PreviewDownloadAmbientAsync(DownloadJob? job)
    {
        if (ReferenceEquals(job, _ambientJob)) return;
        CancelAmbientLoad(); _ambientJob = job;
        if (job is null || UiThemeService.IsHighContrastActive || !UiThemeService.AmbientEnabled)
        { HideDownloadAmbient(); return; }
        if (_ambientPalettes.TryGetValue(job.AppId, out var cached))
        { ShowDownloadAmbient(cached); return; }
        var load = _ambientLoad = new CancellationTokenSource();
        var token = load.Token;
        try
        {
            var model = (DownloadsViewModel)DataContext;
            var service = App.Services.GetRequiredService<IArtworkService>();
            var game = new Game { AppId = job.AppId, Name = job.GameName };
            await service.LoadAsync(game, token);
            ImageSource? image = game.ArtworkImage;
            image ??= ReferenceEquals(model.HeroJob, job) ? model.HeroArtwork : null;
            image ??= await service.LoadHeroAsync(job.AppId, token);
            if (image is null)
            {
                await service.LoadHeadersAsync(new[] { game }, token);
                image = game.HeaderImage;
            }
            if (token.IsCancellationRequested || !ReferenceEquals(_ambientJob, job) || !_ambientAttached) return;
            if (image is null) { HideDownloadAmbient(); return; }
            var brush = CreateDownloadAmbientBrush(image);
            if (_ambientImages.Count >= 64) { var oldest = _ambientImages.Keys.First(); _ambientPalettes.Remove(oldest); _ambientImages.Remove(oldest); }
            _ambientImages[job.AppId] = image;
            _ambientPalettes[job.AppId] = brush;
            ShowDownloadAmbient(brush);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or HttpRequestException or NotSupportedException or ArgumentException)
        { /* Missing artwork leaves the page black. */ }
        finally
        {
            if (ReferenceEquals(_ambientLoad, load)) { _ambientLoad = null; load.Dispose(); }
        }
    }
    private void CancelAmbientLoad()
    { var load = _ambientLoad; _ambientLoad = null; load?.Cancel(); load?.Dispose(); }
    private Brush CreateDownloadAmbientBrush(ImageSource image)
    {
        // Sample every pixel, including neutral artwork. A small saturated logo must
        // not repaint an otherwise white/gray cover. Interpolate the spatial palette
        // once into a frozen surface, keeping the ambient cheap during scrolling.
        const int width = 36, height = 54;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new ImageBrush(image) { Stretch = Stretch.Fill }, null, new Rect(0,0,width,height));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels,width * 4,0);
        const int columns=4, rows=3;
        var colors = new Color[columns*rows];
        for (var region = 0; region < colors.Length; region++)
        {
            double red=0, green=0, blue=0, count=0;
            for (var y=(region/columns) * height/rows; y<(region/columns+1)*height/rows; y++)
            for (var x=(region%columns)*width/columns; x<(region%columns+1)*width/columns; x++)
            {
                var index=(y*width+x)*4;
                if (pixels[index+3]<128) continue;
                var alpha=pixels[index+3]/255d;
                red+=pixels[index+2]/alpha; green+=pixels[index+1]/alpha; blue+=pixels[index]/alpha;
                count++;
            }
            if (count==0) { colors[region]=Color.FromRgb(16,19,25); continue; }
            red/=count; green/=count; blue/=count;
            var luminance=.2126*red+.7152*green+.0722*blue;
            // Gentle chroma boost preserves actual color coverage; neutral areas stay neutral.
            byte Tone(double channel) => (byte)Math.Clamp(Math.Round(12+.38*(luminance+1.35*(channel-luminance))),12,108);
            colors[region]=Color.FromRgb(Tone(red),Tone(green),Tone(blue));
        }
        const int meshWidth=256, meshHeight=96;
        var mesh=new byte[meshWidth*meshHeight*4];
        for (var y=0; y<meshHeight; y++)
        for (var x=0; x<meshWidth; x++)
        {
            var horizontal=x/(double)(meshWidth-1)*(columns-1);
            var column=Math.Min(columns-2,(int)horizontal); var blend=horizontal-column;
            blend=blend*blend*(3-2*blend);
            var vertical=y/(double)(meshHeight-1);
            var rowPosition=vertical*(rows-1);
            var row=Math.Min(rows-2,(int)rowPosition); var rowBlend=rowPosition-row;
            rowBlend=rowBlend*rowBlend*(3-2*rowBlend);
            var topLeft=colors[row*columns+column]; var topRight=colors[row*columns+column+1];
            var bottomLeft=colors[(row+1)*columns+column]; var bottomRight=colors[(row+1)*columns+column+1];
            var fade=1-.36*vertical*vertical*(3-2*vertical);
            var noise=(((x*73856093L)^(y*19349663L))&255)/255d-.5;
            double Mix(byte tl,byte tr,byte bl,byte br) => Math.Clamp(((tl+(tr-tl)*blend)*(1-rowBlend)+(bl+(br-bl)*blend)*rowBlend)*fade+12*(1-fade)+noise,0,255);
            var index=(y*meshWidth+x)*4;
            mesh[index]=(byte)Math.Round(Mix(topLeft.B,topRight.B,bottomLeft.B,bottomRight.B));
            mesh[index+1]=(byte)Math.Round(Mix(topLeft.G,topRight.G,bottomLeft.G,bottomRight.G));
            mesh[index+2]=(byte)Math.Round(Mix(topLeft.R,topRight.R,bottomLeft.R,bottomRight.R));
            mesh[index+3]=255;
        }
        var surface=System.Windows.Media.Imaging.BitmapSource.Create(meshWidth,meshHeight,96,96,PixelFormats.Bgra32,null,mesh,meshWidth*4);
        surface.Freeze();
        var drawing=new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new ImageBrush(surface) { Stretch=Stretch.Fill },null,new RectangleGeometry(new Rect(0,0,1,1))));
        var result=new DrawingBrush(drawing) { Stretch=Stretch.Fill };
        result.Freeze();
        return result;
    }
    private void RefreshDownloadAmbientGeometry()
    {
        if (!_ambientAttached || !UiThemeService.AmbientEnabled || UiThemeService.IsHighContrastActive) return;
        _ambientPalettes.Clear();
        if (_ambientJob is not { } job || !_ambientImages.TryGetValue(job.AppId, out var image)) return;
        var brush = CreateDownloadAmbientBrush(image);
        _ambientPalettes[job.AppId] = brush;
        ShowDownloadAmbient(brush, animate: false);
    }
    private static Brush CreateDownloadPageAmbient(Brush card)
    {
        var area=new RectangleGeometry(new Rect(0,0,1,1));
        var drawing=new DrawingGroup();
        var baseBrush=new SolidColorBrush(Color.FromRgb(16,19,25));
        drawing.Children.Add(new GeometryDrawing(baseBrush,null,area));
        var tint=card.Clone(); tint.Opacity=.48;
        drawing.Children.Add(new GeometryDrawing(tint,null,area));
        var fade=new LinearGradientBrush { StartPoint=new Point(0,0), EndPoint=new Point(0,1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0,16,19,25),0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(48,16,19,25),.45));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(180,16,19,25),1));
        drawing.Children.Add(new GeometryDrawing(fade,null,area));
        var result=new DrawingBrush(drawing) { Stretch=Stretch.Fill }; result.Freeze();
        return result;
    }
    private void ShowDownloadAmbient(Brush brush, bool animate = true)
    {
        if (UiThemeService.IsHighContrastActive) return;
        var incoming = _ambientFrontIsA ? DownloadAmbientB : DownloadAmbientA;
        var outgoing = _ambientFrontIsA ? DownloadAmbientA : DownloadAmbientB;
        FadeAmbient(incoming, 0, false); incoming.Background = brush;
        var pageIncoming=_ambientFrontIsA ? DownloadPageAmbientB : DownloadPageAmbientA;
        var pageOutgoing=_ambientFrontIsA ? DownloadPageAmbientA : DownloadPageAmbientB;
        FadeAmbient(pageIncoming,0,false);
        pageIncoming.Background=CreateDownloadPageAmbient(brush);
        FadeAmbient(pageIncoming,1,animate && MotionPreferences.AnimationsEnabled);
        FadeAmbient(pageOutgoing,0,animate && MotionPreferences.AnimationsEnabled);
        if (_ambientWindow is not null) _ambientWindow.Resources["WorkspaceBackgroundBrush"]=pageIncoming.Background;

        FadeAmbient(incoming, 1, animate && MotionPreferences.AnimationsEnabled);
        FadeAmbient(outgoing, 0, animate && MotionPreferences.AnimationsEnabled);
        _ambientFrontIsA = !_ambientFrontIsA;
    }
    private void HideDownloadAmbient()
    {
        FadeAmbient(DownloadAmbientA, 0, MotionPreferences.AnimationsEnabled);
        FadeAmbient(DownloadAmbientB, 0, MotionPreferences.AnimationsEnabled);
        FadeAmbient(DownloadPageAmbientA, 0, MotionPreferences.AnimationsEnabled);
        FadeAmbient(DownloadPageAmbientB, 0, MotionPreferences.AnimationsEnabled);
        if (_ambientWindow is not null) _ambientWindow.Resources["WorkspaceBackgroundBrush"]=Resources["DeckBackground"];
    }
    private void FadeAmbient(Border surface, double target, bool animate)
    {
        var from = surface.Opacity;
        surface.BeginAnimation(OpacityProperty, null); surface.Opacity = target;
        if (animate) surface.BeginAnimation(OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private void DownloadAmbient_PreferencesChanged(object? sender, EventArgs e)
    {
        var highContrast = UiThemeService.IsHighContrastActive;
        ApplyDeckAccessibility();
        if (_ambientWindow is not null) _ambientWindow.Resources["WorkspaceBackgroundBrush"] = Resources["DeckBackground"];
        if (highContrast || !UiThemeService.AmbientEnabled) { CancelAmbientLoad(); _ambientJob = null; HideDownloadAmbient(); return; }
        if (!MotionPreferences.AnimationsEnabled)
        {
            FadeAmbient(DownloadAmbientA, _ambientJob is not null && _ambientFrontIsA ? 1 : 0, false);
            FadeAmbient(DownloadAmbientB, _ambientJob is not null && !_ambientFrontIsA ? 1 : 0, false); 
            FadeAmbient(DownloadPageAmbientA, _ambientJob is not null && _ambientFrontIsA ? 1 : 0, false);
            FadeAmbient(DownloadPageAmbientB, _ambientJob is not null && !_ambientFrontIsA ? 1 : 0, false);
        }
        _ambientJob = null;
        RefreshAmbientTarget();
    }
}
