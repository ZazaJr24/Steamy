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
    private readonly Button _soundButton = new()
    {
        Content = "Unmute", ToolTip = "Unmute trailer", Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8), Padding = new Thickness(9, 5, 9, 5), FontSize = 11,
        Background = new SolidColorBrush(Color.FromArgb(220, 25, 29, 35)),
        Foreground = Brushes.White, BorderThickness = new Thickness(0)
    };
    private readonly DispatcherTimer _loop = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _hover;
    private CancellationTokenSource? _fallbackHover;
    private MediaElement? _player;
    private MediaElement? _audioPlayer;
    private Task<Uri?>? _audioRequest;
    private HdrTrailerPlayer? _hdrPlayer;
    private FrameworkElement? _owner;
    private Window? _window;
    private CancellationTokenSource? _posterSample;
    internal bool IsPlaying => _player is { Opacity: > 0 } || _hdrPlayer?.IsPlaying == true;
    internal MediaElement? Player => _player;

    public TrailerPreview()
    {
        IsHitTestVisible = true;
        _poster.IsHitTestVisible = false;
        Children.Add(_poster);
        Panel.SetZIndex(_soundButton, 2);
        Children.Add(_soundButton);
        _soundButton.Click += async (_, args) =>
        {
            args.Handled = true;
            if (_player is not { } player) return;
            player.IsMuted = !player.IsMuted;
            player.Volume = player.IsMuted ? 0 : 1;
            _soundButton.Content = player.IsMuted ? "Unmute" : "Mute";
            _soundButton.ToolTip = player.IsMuted ? "Unmute trailer" : "Mute trailer";
            if (_audioPlayer is { } audio) audio.IsMuted = player.IsMuted;
            if (!player.IsMuted) await StartAudioAsync();
        };
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
                if (_audioPlayer is { } audio) audio.Position = player.Position;
                player.Play();
            }
        };
    }

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (TrailerPreview)sender;
        view._poster.Source = view.Poster;
        if(args.Property==PosterProperty) view.WarmPosterColors();
        if (args.Property == AppIdProperty) view.StopPreview();
    }

    private void Attach(object sender, RoutedEventArgs args)
    {
        WarmPosterColors();
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
        if (_window is not null) { _window.Deactivated += Deactivated; _window.StateChanged += WindowStateChanged; }
        MotionPreferences.Changed += PreferencesChanged;
    }

    private void Detach(object sender, RoutedEventArgs args)
    {
        CancelPosterSample();
        StopPreview();
        if (_owner is not null)
        {
            _owner.MouseEnter -= Enter;
            _owner.MouseLeave -= Leave;
            _owner.GotKeyboardFocus -= FocusEntered;
            _owner.LostKeyboardFocus -= FocusLeft;
            _owner.RemoveHandler(Button.ClickEvent, new RoutedEventHandler(CardClicked));
        }
        if (_window is not null) { _window.Deactivated -= Deactivated; _window.StateChanged -= WindowStateChanged; }
        MotionPreferences.Changed -= PreferencesChanged;
        _owner = null; _window = null;
    }

    private void Enter(object sender, MouseEventArgs args) => BeginPreview();
    private void CancelPosterSample()
    { _posterSample?.Cancel(); _posterSample?.Dispose(); _posterSample=null; }
    private async void WarmPosterColors()
    {
        CancelPosterSample();
        if(!IsLoaded || Poster is not { } source) return;
        var request=_posterSample=new CancellationTokenSource();
        var token=request.Token;
        try
        {
            await ArtworkColorSample.ForAsync(source,token);
            if(ReferenceEquals(_posterSample,request) && !token.IsCancellationRequested && IsLoaded)
                (Window.GetWindow(this) as MainWindow)?.QueueCaptionAppearance();
        }
        catch(OperationCanceledException) { }
        catch(Exception error) when(error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
        finally { if(ReferenceEquals(_posterSample,request)) { _posterSample=null; request.Dispose(); } }
    }
    private void Leave(object sender, MouseEventArgs args) => StopPreview();
    private void FocusEntered(object sender, KeyboardFocusChangedEventArgs args) { if (ReferenceEquals(args.OriginalSource, _owner)) BeginPreview(); }
    private void FocusLeft(object sender, KeyboardFocusChangedEventArgs args) { if (_owner?.IsMouseOver != true && _owner?.IsKeyboardFocusWithin != true) StopPreview(); }
    private void Deactivated(object? sender, EventArgs args) => StopPreview();
    private void WindowStateChanged(object? sender, EventArgs args) { if (_window?.WindowState == WindowState.Minimized) StopPreview(); }
    private void CardClicked(object sender, RoutedEventArgs args) => StopPreview();
    private static bool PreviewsEnabled => MotionPreferences.TrailerPreviewsEnabled && MotionPreferences.AnimationsEnabled;
    private void PreferencesChanged(object? sender, EventArgs args) { if (!PreviewsEnabled) StopPreview(); }

    internal async void BeginPreview()
    {
        StopPreview();
        if (!IsLoaded || !PreviewsEnabled || AppId <= 0 || _window?.WindowState == WindowState.Minimized) return;
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
            if (video is null || !IsLoaded || !IsVisible || !PreviewsEnabled || _window?.IsActive == false || _window?.WindowState == WindowState.Minimized) return;
            if (video.IsHdr)
            {
                var hdr = new HdrTrailerPlayer();
                _hdrPlayer = hdr;
                FitPlayer();
                hdr.PreviewActivated += (_, _) =>
                {
                    if (_owner is Button button)
                    {
                        var command = button.Command;
                        var parameter = button.CommandParameter;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
                    }
                };
                hdr.SoundRequested += async (_, _) => await StartAudioAsync();
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
                && IsLoaded && IsVisible && PreviewsEnabled && _window?.IsActive != false && _window?.WindowState != WindowState.Minimized) StartSdr(source);
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

    private async Task StartAudioAsync()
    {
        var hover = _hover;
        if (hover is null) return;
        try
        {
            _audioRequest ??= App.Services.GetRequiredService<ITrailerPreviewService>().GetAudioPreviewAsync(AppId, hover.Token);
            var source = await _audioRequest;
            if (source is null || !ReferenceEquals(hover, _hover) || hover.IsCancellationRequested) return;
            if (_hdrPlayer is { } hdr) { await hdr.SetAudioSourceAsync(source); return; }
            if (_player is not { } video || _audioPlayer is not null) return;
            var audio = _audioPlayer = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
                Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, Volume = 1, IsMuted = video.IsMuted
            };
            audio.MediaOpened += (_, _) =>
            {
                if (ReferenceEquals(_audioPlayer, audio) && _player is { } playing)
                { audio.Position = playing.Position; audio.IsMuted = playing.IsMuted; audio.Play(); }
            };
            Children.Add(audio); audio.Source = source; audio.Play();
        }
        catch (OperationCanceledException) { }
        catch { _audioRequest = null; }
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
        _soundButton.Content = "Unmute";
        _soundButton.ToolTip = "Unmute trailer";
        _soundButton.Visibility = Visibility.Visible;
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
        _soundButton.Visibility = Visibility.Collapsed;
        _audioRequest = null;
        if (_audioPlayer is { } audio)
        {
            _audioPlayer = null; audio.Stop(); audio.Close(); audio.Source = null; Children.Remove(audio);
        }
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
