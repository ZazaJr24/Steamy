using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>Visible/recycled images own cancellable subscriptions, never WPF's implicit URI download cache.</summary>
public static class AsyncArtwork
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached("Url", typeof(object), typeof(AsyncArtwork), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty WidthProperty = DependencyProperty.RegisterAttached("Width", typeof(int), typeof(AsyncArtwork), new PropertyMetadata(400, Changed));
    public static readonly DependencyProperty HeightProperty = DependencyProperty.RegisterAttached("Height", typeof(int), typeof(AsyncArtwork), new PropertyMetadata(0, Changed));
    public static readonly DependencyProperty PrivateProperty = DependencyProperty.RegisterAttached("Private", typeof(bool), typeof(AsyncArtwork), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(AsyncArtwork));
    public static object? GetUrl(DependencyObject owner) => owner.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject owner, object? value) => owner.SetValue(UrlProperty, value);
    public static int GetWidth(DependencyObject owner) => (int)owner.GetValue(WidthProperty);
    public static void SetWidth(DependencyObject owner, int value) => owner.SetValue(WidthProperty, value);
    public static int GetHeight(DependencyObject owner) => (int)owner.GetValue(HeightProperty);
    public static void SetHeight(DependencyObject owner, int value) => owner.SetValue(HeightProperty, value);
    public static bool GetPrivate(DependencyObject owner) => (bool)owner.GetValue(PrivateProperty);
    public static void SetPrivate(DependencyObject owner, bool value) => owner.SetValue(PrivateProperty, value);
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not Image image) return;
        if (image.GetValue(StateProperty) is not State state) image.SetValue(StateProperty, state = new State(image));
        state.Refresh();
    }
    private sealed class State
    {
        private readonly Image _image;
        private CancellationTokenSource? _request;
        private readonly System.Windows.Threading.DispatcherTimer _resize = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        private int _requestedWidth;
        private IDisposable? _viewport;
        private ImagePriority _priority;
        internal State(Image image)
        {
            _image = image;
            image.Loaded += (_, _) => { _viewport?.Dispose(); _viewport = ArtworkViewport.Observe(image, ViewportChanged); Refresh(); };
            image.Unloaded += (_, _) => { _viewport?.Dispose(); _viewport = null; Release(); };
            image.IsVisibleChanged += (_, _) => { if (image.IsVisible) Refresh(); else Release(); };
            image.SizeChanged += (_, _) => { if (image.IsVisible && image.ActualWidth * VisualTreeHelper.GetDpi(image).DpiScaleX > _requestedWidth * 1.2) { _resize.Stop(); _resize.Start(); } };
            _resize.Tick += (_, _) => { _resize.Stop(); if (_image.IsVisible) Refresh(); };
        }
        private void ViewportChanged()
        {
            if (ArtworkViewport.Priority(_image) is not { } priority) Release();
            else if (_image.Source is null && _request is null) Refresh();
            else if (_priority == ImagePriority.Prefetch && priority == ImagePriority.Visible && Uri.TryCreate(GetUrl(_image)?.ToString(), UriKind.Absolute, out var source))
            { _priority = priority; (App.Services.GetService<IImagePipeline>() ?? ImagePipeline.Shared).Prioritize(source); }
        }
        private void Release() { _resize.Stop(); _request?.Cancel(); _request?.Dispose(); _request = null; _image.Source = null; }
        internal async void Refresh()
        {
            Release();
            if (ArtworkViewport.Priority(_image) is not { } priority) return;
            _priority = priority;
            if (GetUrl(_image) is ImageSource source) { _image.Source = source; return; }
            if (!Uri.TryCreate(GetUrl(_image)?.ToString(), UriKind.Absolute, out var uri)) return;
            if (GetPrivate(_image) && !ChatMediaPolicy.IsImageUri(uri)) return;
            var request = _request = new CancellationTokenSource(); var token = request.Token;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(_image);
                var width = Math.Clamp((int)Math.Ceiling(Math.Max(GetWidth(_image), _image.ActualWidth * dpi.DpiScaleX)), 1, 2560);
                width = Math.Min(2560, (width + 63) / 64 * 64); _requestedWidth = width;
                var height = GetHeight(_image) > 0 ? Math.Clamp((int)Math.Ceiling(GetHeight(_image) * dpi.DpiScaleY), 1, 2560) : 0;
                var pipeline = App.Services.GetService<IImagePipeline>() ?? ImagePipeline.Shared;
                var result = await pipeline.LoadAsync(new ImageRequest(uri, width, height, Priority: priority,
                    CachePolicy: GetPrivate(_image) ? ImageCachePolicy.MemoryOnly : ImageCachePolicy.PublicArtwork), token);
                if (_request == request && !token.IsCancellationRequested) _image.Source = result;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException) { }
        }
    }
}
