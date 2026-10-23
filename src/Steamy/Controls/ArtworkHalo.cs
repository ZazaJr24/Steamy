using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>Cached perimeter colors form a soft, close halo around one poster.</summary>
public sealed class ArtworkHalo : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ArtworkHalo), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SourceChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ArtworkHalo), new PropertyMetadata(false, ActiveChanged));
    public static readonly DependencyProperty SpreadProperty = DependencyProperty.Register(nameof(Spread), typeof(double), typeof(ArtworkHalo),
        new FrameworkPropertyMetadata(58d, FrameworkPropertyMetadataOptions.AffectsRender), value => value is double spread && double.IsFinite(spread) && spread is >= 0 and <= 240);
    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(nameof(Intensity), typeof(double), typeof(ArtworkHalo),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, SourceChanged), value => value is double intensity && double.IsFinite(intensity) && intensity is >= 0 and <= 1);
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Spread { get => (double)GetValue(SpreadProperty); set => SetValue(SpreadProperty, value); }
    public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
    private Brush[]? _lights;
    private double _aspect;
    private int _transition;
    private bool _attached, _paused;
    private Window? _window;
    private CancellationTokenSource? _preparation;
    private double _pendingAspect;
    public ArtworkHalo()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += Attach;
        Unloaded += Detach;
        IsVisibleChanged += (_, _) => { if (!IsVisible) Pause(); else { _paused=_window is not null && (!_window.IsActive || _window.WindowState==WindowState.Minimized); UpdateVisibility(); } };
    }
    private void Attach(object sender,RoutedEventArgs args)
    {
        if (_attached) return;
        _attached=true;
        UiThemeService.PaletteChanged+=PreferencesChanged; MotionPreferences.Changed+=PreferencesChanged;
        _window=Window.GetWindow(this);
        if (_window is not null) { _window.Deactivated+=WindowPaused; _window.Activated+=WindowResumed; _window.StateChanged+=WindowResumed; }
        _paused=_window is not null && (!_window.IsActive || _window.WindowState==WindowState.Minimized);
        UpdateVisibility();
    }
    private void Detach(object sender,RoutedEventArgs args)
    {
        _attached=false;
        UiThemeService.PaletteChanged-=PreferencesChanged; MotionPreferences.Changed-=PreferencesChanged;
        if (_window is not null) { _window.Deactivated-=WindowPaused; _window.Activated-=WindowResumed; _window.StateChanged-=WindowResumed; _window=null; }
        Pause(); _lights=null; InvalidateVisual();
    }
    private void WindowPaused(object? sender,EventArgs args)=>Pause();
    private void WindowResumed(object? sender,EventArgs args)
    { _paused=_window is not null && (!_window.IsActive || _window.WindowState==WindowState.Minimized); if(_paused) Pause(); else UpdateVisibility(); }
    private void Pause()
    { _paused=true; CancelPreparation(); ++_transition; BeginAnimation(OpacityProperty,null); Opacity=0; }
    private void CancelPreparation()
    { _preparation?.Cancel(); _preparation?.Dispose(); _preparation=null; }
    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    { var view=(ArtworkHalo)owner; view.CancelPreparation(); view._lights=null; view.Prepare(); }
    private static void ActiveChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((ArtworkHalo)owner).UpdateVisibility();
    private void PreferencesChanged(object? sender, EventArgs args) { UpdateVisibility(); InvalidateVisual(); }
    private void UpdateVisibility()
    {
        var target = _attached && !_paused && IsVisible && IsActive && UiThemeService.AmbientEnabled && !UiThemeService.IsHighContrastActive ? 1d : 0d;
        if (target>0) Prepare(); else CancelPreparation();
        var previous = Opacity;
        var revision = ++_transition;
        Opacity = target;
        if (MotionPreferences.AnimationsEnabled && IsLoaded && !_paused)
        {
            var animation = new DoubleAnimation(previous, target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (revision == _transition) BeginAnimation(OpacityProperty, null); };
            BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        else BeginAnimation(OpacityProperty, null);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info); Prepare();
    }
    private async void Prepare()
    {
        if (!_attached || _paused || !IsVisible || !IsActive || !UiThemeService.AmbientEnabled || UiThemeService.IsHighContrastActive || Source is not { } source || ActualWidth<=0 || ActualHeight<=0) return;
        var aspect = ActualWidth / ActualHeight;
        if (_lights is not null && Math.Abs(aspect-_aspect)<=.05 || _preparation is not null && Math.Abs(aspect-_pendingAspect)<=.05) return;
        CancelPreparation();
        var request=_preparation=new CancellationTokenSource();
        var token=request.Token;
        _pendingAspect=aspect;
        try
        {
            var lights=await PrepareLightsAsync(source,aspect,Intensity,token);
            if (!ReferenceEquals(_preparation,request) || token.IsCancellationRequested || !_attached || _paused) return;
            _lights=lights; _aspect=aspect; InvalidateVisual();
        }
        catch(OperationCanceledException) { }
        catch(Exception error) when(error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
        finally { if(ReferenceEquals(_preparation,request)) { _preparation=null; request.Dispose(); } }
    }
    internal static async Task<Brush[]> PrepareLightsAsync(ImageSource source,double aspect,double intensity,CancellationToken token)
    {
        var sample=await ArtworkColorSample.ForAsync(source,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var lights=new Brush[12];
        for(var index=0;index<lights.Length;index++)
        {
            var column=index%5;
            var x=index<10 ? .06+column*.22 : index==10 ? .04 : .96;
            var y=index<10 ? index<5 ? .08 : .92 : .5;
            var color=sample.ColorAtCore(x,y,aspect);
            Color Tint(byte alpha)=>Color.FromArgb((byte)Math.Round(alpha*intensity),color.R,color.G,color.B);
            var brush=new RadialGradientBrush {RadiusX=.5,RadiusY=.5};
            brush.GradientStops.Add(new(Tint(155),0)); brush.GradientStops.Add(new(Tint(92),.32));
            brush.GradientStops.Add(new(Tint(24),.7)); brush.GradientStops.Add(new(Tint(0),1));
            brush.Freeze(); lights[index]=brush;
        }
        return lights;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (_lights is null || Source is null || !UiThemeService.AmbientEnabled || UiThemeService.IsHighContrastActive || ActualWidth<=0 || ActualHeight<=0) return;
        // No shader, live blur, bitmap capture or extra layout space. The card itself
        // covers the center of the light, leaving only the soft exterior spill.
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < 5; column++)
            dc.DrawEllipse(_lights[row * 5 + column], null,
                new Point(ActualWidth * (.06 + column * .22), row == 0 ? 5 : ActualHeight - 5),
                ActualWidth * .22 + Spread * 18 / 58, Spread);
        dc.DrawEllipse(_lights[10], null, new Point(5, ActualHeight * .5), Spread, ActualHeight * .52);
        dc.DrawEllipse(_lights[11], null, new Point(ActualWidth - 5, ActualHeight * .5), Spread, ActualHeight * .52);
    }
}
