using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>One requested screenshot at a time; closing cancels the request and releases the image.</summary>
public partial class GameScreenshotViewer : UserControl
{
    internal static Func<Uri, CancellationToken, Task<ChatMedia>> ImageLoader = (uri, token) => ChatMediaLoader.LoadAsync(uri, token, 1920);
    private GameScreenshot[] _screenshots = [];
    private int _index;
    private CancellationTokenSource? _request;
    private bool _failed;
    public event EventHandler? CloseRequested;
    public GameScreenshotViewer()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
        SizeChanged += (_, _) => UpdateFrameSize();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) CloseRequested?.Invoke(this, EventArgs.Empty);
            else if (args.Key == Key.Left) Move(-1);
            else if (args.Key == Key.Right) Move(1);
            else return;
            args.Handled = true;
        };
    }
    public void Open(IReadOnlyList<GameScreenshot> screenshots, int index)
    {
        Close();
        if (screenshots.Count == 0) return;
        _screenshots = screenshots.ToArray(); _index = Math.Clamp(index, 0, _screenshots.Length - 1);
        Visibility = Visibility.Visible;
        LoadCurrent(); CloseImageButton.Focus();
    }
    public void Close()
    {
        CancelRequest(); PreviewImage.Source = null; _screenshots = [];
        ViewerStatus.Text = ""; _failed = false; Visibility = Visibility.Collapsed;
    }
    private void UpdateFrameSize()
    {
        var aspect = PreviewImage.Source is BitmapSource image ? image.PixelWidth / (double)Math.Max(1, image.PixelHeight) : 16d / 9;
        var width = Math.Max(0, ActualWidth - 48); var height = Math.Max(0, ActualHeight - 40);
        ViewerCard.Width = Math.Min(width, height * aspect);
        ViewerCard.Height = Math.Min(height, width / aspect);
    }
    private void CancelRequest() { _request?.Cancel(); _request?.Dispose(); _request = null; }
    private void Move(int delta)
    {
        if (_screenshots.Length < 2) return;
        _index = (_index + delta + _screenshots.Length) % _screenshots.Length; LoadCurrent();
    }
    private async void LoadCurrent()
    {
        CancelRequest(); PreviewImage.Source = null; _failed = false;
        ViewerStatus.Text = "Loading screenshot…"; UpdateFrameSize();
        var request = _request = new CancellationTokenSource(); var token = request.Token;
        try
        {
            var media = await ImageLoader(_screenshots[_index].Full, token);
            if (_request != request || token.IsCancellationRequested) return;
            PreviewImage.Source = media.Poster; ViewerStatus.Text = ""; UpdateFrameSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            if (_request != request || token.IsCancellationRequested) return;
            _failed = true; ViewerStatus.Text = "Image unavailable. Click here to try again.";
        }
    }
    private void Retry_Click(object sender, MouseButtonEventArgs args) { if (_failed) LoadCurrent(); }
    private void Close_Click(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
