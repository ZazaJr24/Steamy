using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>One coordinate system for artwork, its continuation and the logo scrim.</summary>
public sealed partial class DashboardBackdropView : FrameworkElement
{
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(nameof(Artwork), typeof(ImageSource), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(null, ArtworkChanged));
    public static readonly DependencyProperty HeroHeightProperty = DependencyProperty.Register(nameof(HeroHeight), typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(480d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty BlendProperty = DependencyProperty.Register(nameof(Blend), typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PreviewAmountProperty = DependencyProperty.Register("PreviewAmount", typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PreviewBlendProperty = DependencyProperty.Register("PreviewBlend", typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush DarkBase = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Brush LightBase = Brushes.White;
    private static readonly Brush PreviewReveal = CreatePreviewReveal();
    private ImageSource? _previous;
    private ImageSource? _previewArtwork;
    private Brush? _previewBrush, _previousPreviewBrush;
    private int _previewRevision;
    private double _previewTop;
    private byte[]? _chromePixels;
    private Size _chromeSize;
    private bool _chromeLight;
    private int _chromeHeight;
    private ScrollViewer? _scrollHost;
    private ScrollContentPresenter? _scrollViewport;
    private BitmapSource? _ambientBitmap, _previousAmbientBitmap;
    private ImageSource? _ambientArtwork;
    private int _ambientAspectKey;
    private CancellationTokenSource? _ambientRender, _previewRender;
    internal BitmapSource? ChromeSnapshot { get; private set; }
    public ImageSource? Artwork { get => (ImageSource?)GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
    public double HeroHeight { get => (double)GetValue(HeroHeightProperty); set => SetValue(HeroHeightProperty, value); }
    private double Blend { get => (double)GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
    internal BitmapSource? AmbientBitmap => _ambientBitmap;

    public DashboardBackdropView()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            UiThemeService.PaletteChanged += ThemeChanged; MotionPreferences.Changed += MotionChanged;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollContentPresenter viewport) _scrollViewport = viewport;
                if (parent is ScrollViewer scroll) { _scrollHost = scroll; scroll.ScrollChanged += ViewportChanged; break; }
            }
            UpdateAmbient(); InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            UiThemeService.PaletteChanged -= ThemeChanged; MotionPreferences.Changed -= MotionChanged;
            if (_scrollHost is not null) _scrollHost.ScrollChanged -= ViewportChanged;
            _scrollHost = null; _scrollViewport = null;
            FinishTransition(); SetPreviewArtwork(null); FinishPreview(); Artwork = null; ClearChromeSnapshot();
        };
    }
    private void ViewportChanged(object sender, ScrollChangedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, _scrollHost)) InvalidateVisual();
    }
    private Rect? VisibleRegion()
    {
        if (_scrollViewport is { ActualWidth: > 0, ActualHeight: > 0 } viewport)
            try { return new Rect(viewport.TranslatePoint(new Point(), this), viewport.RenderSize); }
            catch (InvalidOperationException) { return null; }
        if (_scrollHost is not { ViewportWidth: > 0, ViewportHeight: > 0 } host) return null;
        try { return new Rect(host.TranslatePoint(new Point(), this), new Size(host.ViewportWidth, host.ViewportHeight)); }
        catch (InvalidOperationException) { return null; }
    }
    private void ThemeChanged(object? sender, EventArgs args) { ClearFramePaint(); ClearChromeSnapshot(); UpdateAmbient(); InvalidateVisual(); }
    private void ClearChromeSnapshot() { _chromePixels = null; ChromeSnapshot = null; }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == ArtworkProperty || args.Property == HeroHeightProperty) { ClearFramePaint(); ClearChromeSnapshot(); }
        if (args.Property == HeroHeightProperty) UpdateAmbient();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        ClearChromeSnapshot();
        ClearFramePaint();
        UpdateAmbient();
    }

    private static void CancelRender(ref CancellationTokenSource? request)
    { request?.Cancel(); request?.Dispose(); request = null; }

    private async void UpdateAmbient()
    {
        var artwork = Artwork;
        if (!IsLoaded || artwork is null || ActualWidth <= 0 || !UiThemeService.AmbientEnabled || UiThemeService.IsHighContrastActive)
        {
            CancelRender(ref _ambientRender);
            _ambientArtwork = null; _ambientBitmap = null;
            return;
        }
        var aspect = ActualWidth / Math.Max(1, HeroHeight);
        var key = ArtworkContinuation.AspectKey(aspect);
        if (ReferenceEquals(_ambientArtwork, artwork) && _ambientAspectKey == key) return;
        CancelRender(ref _ambientRender);
        _ambientArtwork = artwork; _ambientAspectKey = key;
        var request = _ambientRender = new CancellationTokenSource();
        var token = request.Token;
        void Apply(BitmapSource bitmap)
        {
            if (_ambientRender != request || !IsLoaded) return;
            _ambientBitmap = bitmap;
            ClearChromeSnapshot(); InvalidateVisual();
            if (Window.GetWindow(this) is MainWindow window) window.QueueCaptionAppearance();
        }
        try
        {
            var pending = ArtworkContinuation.ForAsync(artwork, aspect, token);
            if (pending.IsCompletedSuccessfully) { Apply(pending.Result); return; }
            var bitmap = await pending.ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => Apply(bitmap), System.Windows.Threading.DispatcherPriority.Normal, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        { }
    }

    // Sample the composed artwork AND readability gradient, not the source image alone.
    // A tiny snapshot is reused while scrolling. No live window screenshots or GPU readback.
    internal double RegionLuminance(Rect region)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return UiThemeService.IsLight ? 1 : 0;
        const int width = 80;
        if (_chromePixels is null || _chromeSize != RenderSize || _chromeLight != UiThemeService.IsLight)
        {
            _chromeSize = RenderSize; _chromeLight = UiThemeService.IsLight;
            _chromeHeight = Math.Clamp((int)Math.Ceiling(ActualHeight / ActualWidth * width), 1, 256);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(width / ActualWidth, _chromeHeight / ActualHeight));
                DrawFrame(dc, transition: false);
                dc.Pop();
            }
            var snapshot = new RenderTargetBitmap(width, _chromeHeight, 96, 96, PixelFormats.Pbgra32);
            snapshot.Render(visual); snapshot.Freeze(); ChromeSnapshot = snapshot;
            _chromePixels = new byte[width * _chromeHeight * 4];
            snapshot.CopyPixels(_chromePixels, width * 4, 0);
        }
        double sum = 0;
        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 7; x++)
        {
            var px = Math.Clamp((int)((region.X + region.Width * (x + .5) / 7) / ActualWidth * width), 0, width - 1);
            var py = Math.Clamp((int)((region.Y + region.Height * (y + .5) / 3) / ActualHeight * _chromeHeight), 0, _chromeHeight - 1);
            var i = (py * width + px) * 4;
            static double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            sum += .2126 * Linear(_chromePixels[i + 2]) + .7152 * Linear(_chromePixels[i + 1]) + .0722 * Linear(_chromePixels[i]);
        }
        return sum / 21;
    }

    internal void DrawWindowSurface(DrawingContext drawing, Window window, Rect surfaceBounds)
    {
        var origin = TranslatePoint(new Point(), window);
        surfaceBounds.Offset(-origin.X, -origin.Y);
        drawing.PushTransform(new TranslateTransform(origin.X, origin.Y));
        DrawFrame(drawing, transition: true, surfaceBounds);
        drawing.Pop();
    }
    private void MotionChanged(object? sender, EventArgs args) { if (!MotionPreferences.AnimationsEnabled) { FinishTransition(); FinishPreview(); } }

    internal async void SetPreviewArtwork(ImageSource? artwork, double? top = null)
    {
        _previewTop = Math.Max(HeroHeight, top ?? HeroHeight);
        if (ReferenceEquals(artwork, _previewArtwork)) return;
        CancelRender(ref _previewRender);
        _previewArtwork = artwork;
        var revision = ++_previewRevision;
        if (artwork is not null)
        {
            var request = _previewRender = new CancellationTokenSource();
            var token = request.Token;
            BitmapSource bitmap;
            var aspect = Math.Max(1, ActualWidth) / Math.Max(1, HeroHeight);
                        void ApplyPreview(BitmapSource source)
            {
                if (_previewRender != request || !IsLoaded) return;
                revision = ++_previewRevision;
                _previousPreviewBrush = _previewBrush;
                var imageBrush = new ImageBrush(source) { Stretch = Stretch.Fill };
                imageBrush.Freeze(); _previewBrush = imageBrush;
                AnimatePreview(PreviewBlendProperty, _previousPreviewBrush is null ? 1 : 0, 1, revision);
                AnimatePreview(PreviewAmountProperty, (double)GetValue(PreviewAmountProperty), 1, revision);
            }
            try
            {
                var pending = ArtworkContinuation.ForAsync(artwork, aspect, token, smooth: true);
                if (pending.IsCompletedSuccessfully) { ApplyPreview(pending.Result); return; }
                bitmap = await pending.ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => ApplyPreview(bitmap), System.Windows.Threading.DispatcherPriority.Normal, token);
            }            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { return; }
            return;
        }
        AnimatePreview(PreviewAmountProperty, (double)GetValue(PreviewAmountProperty), artwork is null ? 0 : 1, revision);
    }
    private void AnimatePreview(DependencyProperty property, double from, double to, int revision)
    {
        SetValue(property, to);
        if (!IsLoaded || !MotionPreferences.AnimationsEnabled)
        { BeginAnimation(property, null); if (property == PreviewAmountProperty && to == 0) { _previewBrush = _previousPreviewBrush = null; _previewPaint = null; } return; }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (revision != _previewRevision) return;
            BeginAnimation(property, null);
            if (property == PreviewBlendProperty) { _previousPreviewBrush = null; _previewPaint = null; }
            if (property == PreviewAmountProperty && to == 0)
            { BeginAnimation(PreviewBlendProperty, null); _previewBrush = _previousPreviewBrush = null; _previewPaint = null; }
        };
        BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void FinishPreview()
    {
        _previewPaint = null;
        ++_previewRevision;
        BeginAnimation(PreviewAmountProperty, null); BeginAnimation(PreviewBlendProperty, null);
        SetValue(PreviewAmountProperty, _previewArtwork is null ? 0d : 1d); SetValue(PreviewBlendProperty, 1d);
        _previousPreviewBrush = null;
        if (_previewArtwork is null) _previewBrush = null;
    }
    private static void ArtworkChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var view = (DashboardBackdropView)owner;
        var previousAmbient = view._ambientBitmap;
        view.FinishTransition();
        view._ambientBitmap = null;
        if (view.IsLoaded && view.IsVisible && MotionPreferences.AnimationsEnabled && args.OldValue is ImageSource previous && args.NewValue is ImageSource)
        {
            view._previous = previous;
            view._previousAmbientBitmap = previousAmbient;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) => view.FinishTransition();
            view.BeginAnimation(BlendProperty, animation);
        }
        view.UpdateAmbient(); view.InvalidateVisual();
    }
    private void FinishTransition() { BeginAnimation(BlendProperty, null); Blend = 1; _previous = null; _previousAmbientBitmap = null; _previousPaint = null; }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        // Retain full layout/scroll coordinates, but do not record offscreen
        // backdrop masks. ScrollChanged invalidates this crop before rendering
        // the next offset, including jumps and viewport resizes.
        DrawFrame(drawing, transition: true, region: VisibleRegion());
    }
    private void DrawFrame(DrawingContext drawing, bool transition, Rect? region = null)
    {
        var bounds = new Rect(RenderSize);
        // Keep the artwork's coordinate system while recording only the strip
        // requested by shell glass. A parent clip alone still records full-page
        // opacity-mask layers inside every small glass surface.
        if (region is { } requested)
            bounds.Intersect(requested);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(UiThemeService.IsHighContrastActive ? SystemColors.WindowBrush : !UiThemeService.AmbientEnabled ? UiThemeService.BackgroundBrush : UiThemeService.IsLight ? LightBase : DarkBase, null, bounds);
        if (!UiThemeService.IsHighContrastActive && HeroHeight > 0)
        {
            var paint = GetFramePaint();
            if (transition && _previous is not null && Blend < 1) DrawArtwork(drawing, _previous, 1, bounds, paint);
            if (Artwork is not null) DrawArtwork(drawing, Artwork, !transition || _previous is null ? 1 : Blend, bounds, paint);
            if (!UiThemeService.AmbientEnabled) { drawing.Pop(); return; }
            // Retain the banner's ambient through the upper body; black begins near
            // the middle of the complete page and settles in the lower third.
            drawing.DrawRectangle(paint.BodyFade, null, bounds);
            if (transition && _previewBrush is not null && (double)GetValue(PreviewAmountProperty) > 0)
            {
                var body = new Rect(0, _previewTop, ActualWidth, Math.Max(0, Math.Min(HeroHeight * 2.4, ActualHeight - _previewTop)));
                var visible = Rect.Intersect(body, bounds);
                if (visible.IsEmpty || visible.Height <= 0 || visible.Width <= 0) { drawing.Pop(); return; }
                var preview = GetPreviewPaint(body);
                var amount = (double)GetValue(PreviewAmountProperty) * (UiThemeService.IsLight ? .20 : .65);
                var blend = (double)GetValue(PreviewBlendProperty);
                drawing.PushOpacityMask(preview.Reveal);
                if (preview.Previous is not null) { drawing.PushOpacity(amount * (1 - blend)); drawing.DrawRectangle(preview.Previous, null, visible); drawing.Pop(); }
                drawing.PushOpacity(amount * blend); drawing.DrawRectangle(preview.Current, null, visible); drawing.Pop();
                drawing.Pop();
            }
        }
        drawing.Pop();
    }
    private void DrawArtwork(DrawingContext drawing, ImageSource artwork, double opacity, Rect bounds, FramePaint paint)
    {
        var continuation = ReferenceEquals(artwork, _previous) ? _previousAmbientBitmap : _ambientBitmap ?? _previousAmbientBitmap;
        var images = GetArtworkPaint(artwork, UiThemeService.AmbientEnabled ? continuation : null, paint);
        drawing.PushOpacity(opacity);
        // The full backdrop is composited as one frame during a carousel crossfade.
        drawing.DrawRectangle(!UiThemeService.AmbientEnabled ? UiThemeService.BackgroundBrush : UiThemeService.IsLight ? LightBase : DarkBase, null, bounds);
        if (UiThemeService.AmbientEnabled && images.Ambient is { } ambient)
        {
            drawing.PushOpacity(UiThemeService.IsLight ? .32 : .30);
            DrawImageRegion(drawing, ambient, paint.AmbientBounds, bounds);
            drawing.Pop();
        }
        if (paint.HeroBounds.IntersectsWith(bounds))
        {
            drawing.PushOpacityMask(paint.HeroFade);
            DrawImageRegion(drawing, images.Hero, paint.HeroBounds, bounds);
            drawing.Pop();
        }
        var scrimBounds = Rect.Intersect(paint.ScrimBounds, bounds);
        if (!scrimBounds.IsEmpty)
        {
            drawing.PushOpacityMask(paint.ScrimFade);
            drawing.DrawRectangle(paint.Scrim, null, scrimBounds);
            drawing.Pop();
        }
        drawing.Pop();
    }
    private static void DrawImageRegion(DrawingContext drawing, ImageBrush brush, Rect imageBounds, Rect bounds)
    {
        var visible = Rect.Intersect(imageBounds, bounds);
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return;
        drawing.DrawRectangle(brush, null, visible);
    }
    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private static Brush CreatePreviewReveal()
    {
        var radial = new RadialGradientBrush { Center = new Point(.5, .4), GradientOrigin = new Point(.5, .4), RadiusX = 1.4, RadiusY = .8 };
        radial.GradientStops.Add(new GradientStop(Colors.White, 0));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(220, 255, 255, 255), .5));
        radial.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        var topFade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, alpha) in new[] { (0d, (byte)0), (.03, (byte)40), (.06, (byte)128), (.09, (byte)215), (.12, (byte)255), (.72, (byte)255), (.9, (byte)100), (1d, (byte)0) })
            topFade.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 255, 255, 255), offset));
        var group = new DrawingGroup { OpacityMask = topFade };
        group.Children.Add(new GeometryDrawing(radial, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        return Freeze(new DrawingBrush(group) { Stretch = Stretch.Fill });
    }
}
