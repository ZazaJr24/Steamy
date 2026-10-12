using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>A poster that becomes one muted trailer after a deliberate card hover.</summary>
public sealed class TrailerPreview : Grid
{
    public static readonly DependencyProperty AppIdProperty = DependencyProperty.Register(nameof(AppId), typeof(int), typeof(TrailerPreview), new PropertyMetadata(0, SourceChanged));
    public static readonly DependencyProperty PosterProperty = DependencyProperty.Register(nameof(Poster), typeof(ImageSource), typeof(TrailerPreview), new PropertyMetadata(null, SourceChanged));
    public int AppId { get => (int)GetValue(AppIdProperty); set => SetValue(AppIdProperty, value); }
    public ImageSource? Poster { get => (ImageSource?)GetValue(PosterProperty); set => SetValue(PosterProperty, value); }
    private static WeakReference<TrailerPreview>? _active;
    private readonly Image _poster = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly DispatcherTimer _loop = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _hover;
    private CancellationTokenSource? _fallbackHover;
    private MediaElement? _player;
    private HdrTrailerPlayer? _hdrPlayer;
    private FrameworkElement? _owner;
    private Window? _window;
    internal bool IsPlaying => _player is { Opacity: > 0 } || _hdrPlayer?.IsPlaying == true;
    internal MediaElement? Player => _player;

    public TrailerPreview()
    {
        IsHitTestVisible = false;
        Children.Add(_poster);
        RoundedClip.SetRadius(this, 14);
        Loaded += Attach;
        Unloaded += Detach;
        SizeChanged += (_, _) => FitPlayer();
        _loop.Tick += (_, _) =>
        {
            // The cached fragmented MP4 contains the opening 24 seconds. Its original
            // duration can be longer, so loop before reaching the final cached fragment.
            if (_player is { } player && player.Position >= TimeSpan.FromSeconds(21))
            {
                player.SpeedRatio = 1;
                player.Position = TimeSpan.FromSeconds(3);
                player.Play();
            }
        };
    }

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (TrailerPreview)sender;
        view._poster.Source = view.Poster;
        if (args.Property == AppIdProperty) view.StopPreview();
    }

    private void Attach(object sender, RoutedEventArgs args)
    {
        DependencyObject? ancestor = VisualTreeHelper.GetParent(this);
        while (ancestor is not null && ancestor is not Button) ancestor = VisualTreeHelper.GetParent(ancestor);
        _owner = ancestor as FrameworkElement;
        if (_owner is null) return;
        _owner.MouseEnter += Enter;
        _owner.MouseLeave += Leave;
        _owner.GotKeyboardFocus += FocusEntered;
        _owner.LostKeyboardFocus += FocusLeft;
        _owner.AddHandler(Button.ClickEvent, new RoutedEventHandler(CardClicked));
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Deactivated += Deactivated;
        MotionPreferences.Changed += PreferencesChanged;
    }

    private void Detach(object sender, RoutedEventArgs args)
    {
        StopPreview();
        if (_owner is not null)
        {
            _owner.MouseEnter -= Enter;
            _owner.MouseLeave -= Leave;
            _owner.GotKeyboardFocus -= FocusEntered;
            _owner.LostKeyboardFocus -= FocusLeft;
            _owner.RemoveHandler(Button.ClickEvent, new RoutedEventHandler(CardClicked));
        }
        if (_window is not null) _window.Deactivated -= Deactivated;
        MotionPreferences.Changed -= PreferencesChanged;
        _owner = null; _window = null;
    }

    private void Enter(object sender, MouseEventArgs args) => BeginPreview();
    private void Leave(object sender, MouseEventArgs args) => StopPreview();
    private void FocusEntered(object sender, KeyboardFocusChangedEventArgs args) => BeginPreview();
    private void FocusLeft(object sender, KeyboardFocusChangedEventArgs args) { if (_owner?.IsMouseOver != true) StopPreview(); }
    private void Deactivated(object? sender, EventArgs args) => StopPreview();
    private void CardClicked(object sender, RoutedEventArgs args) => StopPreview();
    private static bool PreviewsEnabled => MotionPreferences.TrailerPreviewsEnabled && MotionPreferences.AnimationsEnabled;
    private void PreferencesChanged(object? sender, EventArgs args) { if (!PreviewsEnabled) StopPreview(); }

    internal async void BeginPreview()
    {
        StopPreview();
        if (!IsLoaded || !PreviewsEnabled || AppId <= 0) return;
        if (_active is not null && _active.TryGetTarget(out var previous) && previous != this) previous.StopPreview();
        _active = new(this);
        var hover = _hover = new CancellationTokenSource();
        var token = hover.Token;
        try
        {
            await Task.Delay(450, token);
            var service = App.Services.GetRequiredService<ITrailerPreviewService>();
            var video = await service.GetVideoPreviewAsync(AppId, token);
            token.ThrowIfCancellationRequested();
            if (video is null || !IsLoaded || !IsVisible || !PreviewsEnabled || _window?.IsActive == false) return;
            if (video.IsHdr)
            {
                var hdr = new HdrTrailerPlayer();
                _hdrPlayer = hdr;
                FitPlayer();
                hdr.FallbackRequested += (_, _) => _ = StartSdrFallbackAsync(service, hover);
                Children.Add(hdr);
                try { await hdr.StartAsync(video.Source, token); }
                catch (OperationCanceledException) { throw; }
                catch { await StartSdrFallbackAsync(service, hover); }
                return;
            }
            StartSdr(video.Source);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (ReferenceEquals(_hover, hover)) StopPreview(); }
    }

    private async Task StartSdrFallbackAsync(ITrailerPreviewService service, CancellationTokenSource hover)
    {
        if (!ReferenceEquals(_hover, hover) || ReferenceEquals(_fallbackHover, hover)) return;
        _fallbackHover = hover;
        var token = hover.Token;
        CloseHdrPlayer();
        try
        {
            var source = await service.GetPreviewAsync(AppId, token);
            if (source is not null && ReferenceEquals(_hover, hover) && !token.IsCancellationRequested
                && IsLoaded && IsVisible && PreviewsEnabled && _window?.IsActive != false) StartSdr(source);
        }
        catch (OperationCanceledException) { }
        catch { if (ReferenceEquals(_hover, hover)) StopPreview(); }
    }

    private void StartSdr(Uri source)
    {
            if (_player is not null) return;
            var player = new ViewportMediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
                IsMuted = true, Volume = 0, SpeedRatio = 1, ScrubbingEnabled = false, Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false, Opacity = 0
            };
            _player = player;
            FitPlayer();
            player.MediaOpened += Opened;
            player.MediaFailed += Failed;
            player.MediaEnded += Ended;
            Children.Add(player);
            player.Source = source;
            player.Play();
    }

    private void FitPlayer()
    {
        // Media's natural dimensions must never participate in the card's layout.
        // Both players follow the fixed poster viewport, including during resize.
        FrameworkElement? player = _hdrPlayer ?? (FrameworkElement?)_player;
        if (player is null) return;
        player.Width = Math.Max(1, ActualWidth);
        player.Height = Math.Max(1, ActualHeight);
        player.HorizontalAlignment = HorizontalAlignment.Center;
        player.VerticalAlignment = VerticalAlignment.Center;
    }

    private void CloseHdrPlayer()
    {
        if (_hdrPlayer is not { } player) return;
        _hdrPlayer = null;
        Children.Remove(player);
        player.ClosePreview();
    }

    private void Opened(object sender, RoutedEventArgs args)
    {
        if (sender is not MediaElement player || !ReferenceEquals(player, _player) || _hover?.IsCancellationRequested != false) return;
        player.SpeedRatio = 1;
        player.Position = TimeSpan.FromSeconds(3);
        player.Opacity = 1;
        player.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
        player.Play();
        _loop.Start();
    }
    private void Failed(object? sender, ExceptionRoutedEventArgs args)
    {
        if (ReferenceEquals(sender, _player)) StopPreview();
    }
    private void Ended(object sender, RoutedEventArgs args)
    {
        if (_player is null || !ReferenceEquals(sender, _player)) return;
        _player.Position = TimeSpan.Zero;
        _player.Play();
    }

    internal void StopPreview()
    {
        CloseHdrPlayer();
        _hover?.Cancel();
        _hover?.Dispose();
        _hover = null;
        _fallbackHover = null;
        _loop.Stop();
        if (_player is { } player)
        {
            _player = null;
            player.MediaOpened -= Opened;
            player.MediaFailed -= Failed;
            player.MediaEnded -= Ended;
            player.BeginAnimation(OpacityProperty, null);
            player.Stop();
            player.Close();
            player.Source = null;
            Children.Remove(player);
        }
        if (_active?.TryGetTarget(out var active) == true && active == this) _active = null;
    }
}
