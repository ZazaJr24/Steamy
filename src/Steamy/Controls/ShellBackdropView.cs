using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>Paints matching portions of one image in the workspace and behind the glass.</summary>
public sealed class ShellBackdropView : FrameworkElement
{
    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(nameof(BaseBrush), typeof(Brush), typeof(ShellBackdropView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, AppearanceChanged));
    public static readonly DependencyProperty FadeColorProperty = DependencyProperty.Register(nameof(FadeColor), typeof(Color), typeof(ShellBackdropView), new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsGlassProperty = DependencyProperty.Register(nameof(IsGlass), typeof(bool), typeof(ShellBackdropView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, AppearanceChanged));
    public static readonly DependencyProperty PreferSharpArtworkProperty = DependencyProperty.Register(nameof(PreferSharpArtwork), typeof(bool), typeof(ShellBackdropView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IncludePageContentProperty = DependencyProperty.Register(nameof(IncludePageContent), typeof(bool), typeof(ShellBackdropView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UseLiquidRefractionProperty = DependencyProperty.Register(nameof(UseLiquidRefraction), typeof(bool), typeof(ShellBackdropView), new FrameworkPropertyMetadata(true, AppearanceChanged));
    public static readonly DependencyProperty GlassBlurIntensityProperty = DependencyProperty.Register(nameof(GlassBlurIntensity), typeof(double), typeof(ShellBackdropView), new FrameworkPropertyMetadata(0.08d, FrameworkPropertyMetadataOptions.AffectsRender, AppearanceChanged), value => value is double intensity && double.IsFinite(intensity) && intensity is >= 0 and <= 2);
    public Brush BaseBrush { get => (Brush)GetValue(BaseBrushProperty); set => SetValue(BaseBrushProperty, value); }
    public Color FadeColor { get => (Color)GetValue(FadeColorProperty); set => SetValue(FadeColorProperty, value); }
    public bool IsGlass { get => (bool)GetValue(IsGlassProperty); set => SetValue(IsGlassProperty, value); }
    public bool PreferSharpArtwork { get => (bool)GetValue(PreferSharpArtworkProperty); set => SetValue(PreferSharpArtworkProperty, value); }
    public bool IncludePageContent { get => (bool)GetValue(IncludePageContentProperty); set => SetValue(IncludePageContentProperty, value); }
    public bool UseLiquidRefraction { get => (bool)GetValue(UseLiquidRefractionProperty); set => SetValue(UseLiquidRefractionProperty, value); }
    public double GlassBlurIntensity { get => (double)GetValue(GlassBlurIntensityProperty); set => SetValue(GlassBlurIntensityProperty, value); }
    public static readonly DependencyProperty IsPageBackdropProperty = DependencyProperty.Register(nameof(IsPageBackdrop), typeof(bool), typeof(ShellBackdropView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsPageBackdrop { get => (bool)GetValue(IsPageBackdropProperty); set => SetValue(IsPageBackdropProperty, value); }
    private IShellBackdropService? _backdrop;
    private Window? _window;
    private GlassyEffect? _glassEffect;
    private readonly BlurEffect _clearGlassEffect = new() { Radius = 2.6, RenderingBias = RenderingBias.Quality };
    private ClearGlassEffect? _clearRefraction;
    private ImageSource? _artwork, _blurred, _previousArtwork, _previousBlurred;
    private int _transitionRevision;
    private static readonly DependencyProperty TransitionProgressProperty = DependencyProperty.Register("TransitionProgress", typeof(double), typeof(ShellBackdropView), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush WashMask = VerticalMask((0, 0.9), (0.35, 0.8), (0.6, 0.35), (0.9, 0.05), (1, 0));
    private static readonly Brush ArtworkMask = VerticalMask((0, 0), (0.06, 1), (0.45, 1), (0.72, 0.35), (0.93, 0), (1, 0));
    private static readonly Brush ChromeShade = CreateChromeShade();

    public ShellBackdropView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            _backdrop = App.Services.GetService<IShellBackdropService>();
            _window = Window.GetWindow(this);
            if (_backdrop is not null) _backdrop.Changed += Refresh;
            if (_window is not null) _window.SizeChanged += WindowResized;
            MotionPreferences.Changed += Refresh;
            Refresh(this, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            if (_backdrop is not null) _backdrop.Changed -= Refresh;
            if (_window is not null) _window.SizeChanged -= WindowResized;
            MotionPreferences.Changed -= Refresh;
            _backdrop = null; _window = null;
            _artwork = _blurred = null;
            StopTransition();
            Effect = null;
        };
    }

    private static void AppearanceChanged(DependencyObject view, DependencyPropertyChangedEventArgs args) => ((ShellBackdropView)view).UpdateGlassEffect();

    private void Refresh(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_artwork, _backdrop?.Artwork))
        {
            var previous = _artwork;
            var previousBlurred = _blurred;
            StopTransition();
            _artwork = _backdrop?.Artwork;
            _blurred = _backdrop?.BlurredArtwork;
            if (previous is not null && _artwork is not null && MotionPreferences.AnimationsEnabled)
            {
                _previousArtwork = previous;
                _previousBlurred = previousBlurred;
                var revision = _transitionRevision;
                var transition = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
                transition.Completed += (_, _) => { if (revision == _transitionRevision) StopTransition(); };
                BeginAnimation(TransitionProgressProperty, transition);
            }
        }
        if (!MotionPreferences.AnimationsEnabled) StopTransition();
        UpdateGlassEffect();
        InvalidateVisual();
    }
    private void StopTransition()
    {
        _transitionRevision++;
        BeginAnimation(TransitionProgressProperty, null);
        _previousArtwork = _previousBlurred = null;
    }
    private void WindowResized(object sender, SizeChangedEventArgs e) => InvalidateVisual();

    private void UpdateGlassEffect()
    {
        if (IsGlass && !UseLiquidRefraction && MotionPreferences.BackdropBlurEnabled && !UiThemeService.IsHighContrastActive)
        {
            if ((RenderCapability.Tier >> 16) >= 2 && RenderCapability.IsPixelShaderVersionSupported(3, 0))
            {
                _clearRefraction ??= new ClearGlassEffect();
                _clearRefraction.Size = new Point(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
                Effect = _clearRefraction;
            }
            else Effect = _clearGlassEffect;
            return;
        }
        if (IsGlass && GlassBlurIntensity > 0 && MotionPreferences.BackdropBlurEnabled && !UiThemeService.IsHighContrastActive
            && (RenderCapability.Tier >> 16) >= 2
            && System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(3, 0))
        {
            _glassEffect ??= new GlassyEffect();
            _glassEffect.TextureSize = new Point(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            _glassEffect.GlassCenter = new Point(ActualWidth * 0.5, ActualHeight * 0.5);
            _glassEffect.GlassSize = new Point(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            _glassEffect.BlurIntensity = (float)GlassBlurIntensity;
            Effect = _glassEffect;
        }
        else Effect = null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGlassEffect();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        var origin = _window is null ? new Point() : TranslatePoint(new Point(), _window);
        var width = _window?.ActualWidth ?? ActualWidth;
        var height = _window?.ActualHeight ?? ActualHeight;
        dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
        if (TemplatedParent is Wpf.Ui.Controls.NavigationView { IsPaneOpen: true } navigation
            && navigation.Template.FindName("MenuLauncher", navigation) is FrameworkElement launcher && launcher.IsAncestorOf(this))
        {
            dc.DrawRectangle(BaseBrush, null, new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)));
            dc.Pop(); dc.Pop(); return;
        }
        if (!IsPageBackdrop && !UiThemeService.IsHighContrastActive && _window is MainWindow { DashboardSurface: { IsLoaded: true } dashboard })
        {
            dashboard.DrawWindowSurface(dc, _window);
            if (IncludePageContent && IsGlass && dashboard.Parent is FrameworkElement { Parent: FrameworkElement content })
            {
                // The toolbar is outside this page visual: no recursive capture, no
                // full-window bitmap or blur. Sample only the strip behind the glass.
                var cropOrigin = TranslatePoint(new Point(), content);
                var contentOffset = VisualTreeHelper.GetOffset(content);
                cropOrigin.Offset(contentOffset.X, contentOffset.Y);
                var contentBrush = new VisualBrush(content) { AutoLayoutContent = false,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(cropOrigin, RenderSize), Stretch = Stretch.Fill };
                dc.DrawRectangle(contentBrush, null, new Rect(origin, RenderSize));
            }
            dc.Pop(); dc.Pop(); return;
        }
        if (!UiThemeService.AmbientEnabled && !UiThemeService.IsHighContrastActive)
        {
            dc.DrawRectangle(UiThemeService.BackgroundBrush, null, new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)));
            dc.Pop(); dc.Pop(); return;
        }
        dc.DrawRectangle(BaseBrush, null, new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)));
        if (IsPageBackdrop && !UiThemeService.IsHighContrastActive)
        {
            // The theme already paints stationary ambient light. Avoid a second colour wash
            // and keep the same background behind the page, navigation and glass surfaces.
            dc.Pop(); dc.Pop(); return;
        }
        if (_backdrop is not null && !UiThemeService.IsHighContrastActive && (!IsGlass || MotionPreferences.BackdropBlurEnabled))
        {
            var progress = (double)GetValue(TransitionProgressProperty);
            if (IsPageBackdrop && _blurred is not null && MotionPreferences.BackdropBlurEnabled)
            {
                // Reuse the small frozen blur; never blur the live page or run a full-window shader.
                dc.PushOpacity(0.42);
                dc.DrawRectangle(new ImageBrush(_blurred) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, width, height));
                dc.Pop();
                dc.Pop(); dc.Pop();
                return;
            }
            var softenArtwork = IsGlass && !PreferSharpArtwork;
            DrawArtwork(dc, softenArtwork ? _previousBlurred : _previousArtwork, _previousBlurred, width, 1 - progress);
            DrawArtwork(dc, softenArtwork ? _blurred : _artwork, _blurred, width, progress);
            if (_artwork is not null)
                dc.DrawRectangle(ChromeShade, null, new Rect(0, 0, width, 150));
        }
        dc.Pop(); dc.Pop();
    }

    private void DrawArtwork(DrawingContext dc, ImageSource? image, ImageSource? blurred, double width, double opacity)
    {
        if (image is null || opacity <= 0) return;
        dc.PushOpacity(opacity);
            var heroHeight = _backdrop!.HeroHeight + _backdrop.HeroTop;
            var top = -_backdrop.ScrollOffset;
            // Carry the artwork's color into the shelves without enlarging the foreground
            // figures. Both layers fade to zero before their bounds, so no panel edge shows.
            var washBounds = new Rect(0, top, width, heroHeight + 280);
            dc.PushOpacityMask(WashMask);
            dc.DrawRectangle(new ImageBrush(blurred)
            {
                Stretch = Stretch.UniformToFill,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 32, width, heroHeight + 32),
                TileMode = TileMode.FlipY
            }, null, washBounds);
            dc.Pop();

            var bounds = new Rect(0, top + 32, width, heroHeight + 32);
            dc.PushOpacityMask(ArtworkMask);
            dc.DrawRectangle(new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center }, null, bounds);
            dc.Pop();
        dc.Pop();
    }

    private static Brush VerticalMask(params (double Offset, double Opacity)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, opacity) in stops)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity), 255, 255, 255), offset));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateChromeShade()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(72, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(24, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }
}
