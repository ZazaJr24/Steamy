using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>One coordinate system for artwork, its continuation and the logo scrim.</summary>
public sealed class DashboardBackdropView : FrameworkElement
{
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(nameof(Artwork), typeof(ImageSource), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(null, ArtworkChanged));
    public static readonly DependencyProperty HeroHeightProperty = DependencyProperty.Register(nameof(HeroHeight), typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(480d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty BlendProperty = DependencyProperty.Register(nameof(Blend), typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PreviewAmountProperty = DependencyProperty.Register("PreviewAmount", typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PreviewBlendProperty = DependencyProperty.Register("PreviewBlend", typeof(double), typeof(DashboardBackdropView), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush DarkBase = Freeze(new SolidColorBrush(Color.FromRgb(13, 17, 23)));
    private static readonly Brush LightBase = Freeze(new SolidColorBrush(Color.FromRgb(232, 236, 241)));
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
    internal BitmapSource? ChromeSnapshot { get; private set; }
    public ImageSource? Artwork { get => (ImageSource?)GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
    public double HeroHeight { get => (double)GetValue(HeroHeightProperty); set => SetValue(HeroHeightProperty, value); }
    private double Blend { get => (double)GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
    internal BitmapSource? AmbientBitmap => Artwork is null || ActualWidth <= 0 ? null : ArtworkContinuation.For(Artwork, ActualWidth / Math.Max(1, HeroHeight));

    public DashboardBackdropView()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => { UiThemeService.PaletteChanged += ThemeChanged; MotionPreferences.Changed += MotionChanged; InvalidateVisual(); };
        Unloaded += (_, _) => { UiThemeService.PaletteChanged -= ThemeChanged; MotionPreferences.Changed -= MotionChanged; FinishTransition(); SetPreviewArtwork(null); FinishPreview(); };
    }
    private void ThemeChanged(object? sender, EventArgs args) { ClearChromeSnapshot(); InvalidateVisual(); }
    private void ClearChromeSnapshot() { _chromePixels = null; ChromeSnapshot = null; }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == ArtworkProperty || args.Property == HeroHeightProperty) ClearChromeSnapshot();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        ClearChromeSnapshot();
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

    internal void DrawWindowSurface(DrawingContext drawing, Window window)
    {
        var origin = TranslatePoint(new Point(), window);
        drawing.PushTransform(new TranslateTransform(origin.X, origin.Y));
        DrawFrame(drawing, transition: true);
        drawing.Pop();
    }
    private void MotionChanged(object? sender, EventArgs args) { if (!MotionPreferences.AnimationsEnabled) { FinishTransition(); FinishPreview(); } }

    internal void SetPreviewArtwork(ImageSource? artwork, double? top = null)
    {
        _previewTop = Math.Max(HeroHeight, top ?? HeroHeight);
        if (ReferenceEquals(artwork, _previewArtwork)) return;
        _previewArtwork = artwork;
        var revision = ++_previewRevision;
        if (artwork is not null)
        {
            _previousPreviewBrush = _previewBrush;
            var imageBrush = new ImageBrush(ArtworkContinuation.For(artwork, Math.Max(1, ActualWidth) / Math.Max(1, HeroHeight), smooth: true))
            { Stretch = Stretch.Fill };
            imageBrush.Freeze();
            _previewBrush = imageBrush;
            AnimatePreview(PreviewBlendProperty, _previousPreviewBrush is null ? 1 : 0, 1, revision);
        }
        AnimatePreview(PreviewAmountProperty, (double)GetValue(PreviewAmountProperty), artwork is null ? 0 : 1, revision);
    }
    private void AnimatePreview(DependencyProperty property, double from, double to, int revision)
    {
        SetValue(property, to);
        if (!IsLoaded || !MotionPreferences.AnimationsEnabled)
        { BeginAnimation(property, null); if (property == PreviewAmountProperty && to == 0) _previewBrush = _previousPreviewBrush = null; return; }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(360))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (revision != _previewRevision) return;
            BeginAnimation(property, null);
            if (property == PreviewBlendProperty) _previousPreviewBrush = null;
            if (property == PreviewAmountProperty && to == 0)
            { BeginAnimation(PreviewBlendProperty, null); _previewBrush = _previousPreviewBrush = null; }
        };
        BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void FinishPreview()
    {
        ++_previewRevision;
        BeginAnimation(PreviewAmountProperty, null); BeginAnimation(PreviewBlendProperty, null);
        SetValue(PreviewAmountProperty, _previewArtwork is null ? 0d : 1d); SetValue(PreviewBlendProperty, 1d);
        _previousPreviewBrush = null;
        if (_previewArtwork is null) _previewBrush = null;
    }
    private static void ArtworkChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var view = (DashboardBackdropView)owner;
        view.FinishTransition();
        if (view.IsLoaded && view.IsVisible && MotionPreferences.AnimationsEnabled && args.OldValue is ImageSource previous && args.NewValue is ImageSource)
        {
            view._previous = previous;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) => view.FinishTransition();
            view.BeginAnimation(BlendProperty, animation);
        }
        view.InvalidateVisual();
    }
    private void FinishTransition() { BeginAnimation(BlendProperty, null); Blend = 1; _previous = null; }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        DrawFrame(drawing, transition: true);
    }
    private void DrawFrame(DrawingContext drawing, bool transition)
    {
        var bounds = new Rect(RenderSize);
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(UiThemeService.IsHighContrastActive ? SystemColors.WindowBrush : !UiThemeService.AmbientEnabled ? UiThemeService.BackgroundBrush : UiThemeService.IsLight ? LightBase : DarkBase, null, bounds);
        if (!UiThemeService.IsHighContrastActive && HeroHeight > 0)
        {
            if (transition && _previous is not null && Blend < 1) DrawArtwork(drawing, _previous, 1);
            if (Artwork is not null) DrawArtwork(drawing, Artwork, !transition || _previous is null ? 1 : Blend);
            if (!UiThemeService.AmbientEnabled) { drawing.Pop(); return; }
            // Retain the banner's ambient through the upper body; black begins near
            // the middle of the complete page and settles in the lower third.
            var end = Math.Min(ActualHeight, Math.Max(HeroHeight * 1.95, ActualHeight * .72));
            var black = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, Math.Min(end, Math.Max(HeroHeight * 1.12, ActualHeight * .45))), EndPoint = new Point(0, end) };
            black.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            black.GradientStops.Add(new GradientStop(Color.FromArgb(160, 0, 0, 0), .5));
            black.GradientStops.Add(new GradientStop(Colors.Black, 1)); black.Freeze();
            drawing.DrawRectangle(black, null, bounds);
            if (transition && _previewBrush is not null && (double)GetValue(PreviewAmountProperty) > 0)
            {
                var body = new Rect(0, _previewTop, ActualWidth, Math.Max(0, ActualHeight - _previewTop));
                var amount = (double)GetValue(PreviewAmountProperty) * .58;
                var blend = (double)GetValue(PreviewBlendProperty);
                drawing.PushOpacityMask(PreviewReveal);
                if (_previousPreviewBrush is not null) { drawing.PushOpacity(amount * (1 - blend)); drawing.DrawRectangle(_previousPreviewBrush, null, body); drawing.Pop(); }
                drawing.PushOpacity(amount * blend); drawing.DrawRectangle(_previewBrush, null, body); drawing.Pop();
                drawing.Pop();
            }
        }
        drawing.Pop();
    }
    private void DrawArtwork(DrawingContext drawing, ImageSource artwork, double opacity)
    {
        var hero = new Rect(0, 0, ActualWidth, HeroHeight);
        var ambient = new Rect(0, 0, ActualWidth, HeroHeight * ArtworkContinuation.HeightInHeroes);
        drawing.PushOpacity(opacity);
        // The full backdrop is composited as one frame during a carousel crossfade.
        drawing.DrawRectangle(!UiThemeService.AmbientEnabled ? UiThemeService.BackgroundBrush : UiThemeService.IsLight ? LightBase : DarkBase, null, new Rect(RenderSize));
        if (UiThemeService.IsLight && UiThemeService.AmbientEnabled)
        {
            // Continue into a dark body without a hard seam or white text on a pale base.
            var bodyBase = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, HeroHeight * .65), EndPoint = new Point(0, HeroHeight) };
            bodyBase.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            bodyBase.GradientStops.Add(new GradientStop(Colors.Black, 1)); bodyBase.Freeze();
            drawing.DrawRectangle(bodyBase, null, new Rect(RenderSize));
        }
        if (UiThemeService.AmbientEnabled)
        {
            if (UiThemeService.IsLight) drawing.PushOpacity(.65);
            drawing.DrawImage(ArtworkContinuation.For(artwork, ActualWidth / HeroHeight), ambient);
            if (UiThemeService.IsLight) drawing.Pop();
        }
        var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(), EndPoint = new Point(0, HeroHeight) };
        fade.GradientStops.Add(new GradientStop(Colors.White, 0));
        fade.GradientStops.Add(new GradientStop(Colors.White, .4));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(210, 255, 255, 255), .6));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(45, 255, 255, 255), .85));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fade.Freeze();
        drawing.PushOpacityMask(fade);
        drawing.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, hero);
        drawing.Pop();
        var scrim = new LinearGradientBrush { StartPoint = new Point(), EndPoint = new Point(1, 0) };
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(215, 7, 11, 17), 0));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(180, 7, 11, 17), .3));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(45, 7, 11, 17), .65));
        scrim.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        scrim.Freeze();
        if (!UiThemeService.AmbientEnabled) ambient = hero;
        var scrimFade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(), EndPoint = new Point(0, ambient.Height) };
        scrimFade.GradientStops.Add(new GradientStop(Colors.White, 0));
        scrimFade.GradientStops.Add(new GradientStop(Colors.White, .22));
        scrimFade.GradientStops.Add(new GradientStop(Color.FromArgb(130, 255, 255, 255), .45));
        scrimFade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        scrimFade.Freeze();
        drawing.PushOpacityMask(scrimFade);
        drawing.DrawRectangle(scrim, null, ambient);
        drawing.Pop();
        drawing.Pop();
    }
    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private static Brush CreatePreviewReveal()
    {
        var radial = new RadialGradientBrush { Center = new Point(.5, .4), GradientOrigin = new Point(.5, .4), RadiusX = 1.4, RadiusY = .8 };
        radial.GradientStops.Add(new GradientStop(Colors.White, 0));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(220, 255, 255, 255), .5));
        radial.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        var topFade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, alpha) in new[] { (0d, (byte)0), (.03, (byte)40), (.06, (byte)128), (.09, (byte)215), (.12, (byte)255) })
            topFade.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 255, 255, 255), offset));
        var group = new DrawingGroup { OpacityMask = topFade };
        group.Children.Add(new GeometryDrawing(radial, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        return Freeze(new DrawingBrush(group) { Stretch = Stretch.Fill });
    }
}
