using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Steamy.Controls;

/// <summary>Shares decoded artwork across recycled rows with a bounded, UI-thread cache.</summary>
public sealed class ArtworkImageConverter : IValueConverter
{
    private const int Capacity = 96;
    private readonly Dictionary<(string Url, int Width), BitmapImage> _images = new();
    private readonly Queue<(string Url, int Width)> _order = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http")) return null;
        var width = int.TryParse(parameter?.ToString(), out var requested) ? Math.Clamp(requested, 64, 1920) : 320;
        var key = (url, width);
        if (_images.TryGetValue(key, out var existing)) return existing;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.UriSource = uri;
            image.EndInit();
            if (_images.Count >= Capacity) _images.Remove(_order.Dequeue());
            _images.Add(key, image);
            _order.Enqueue(key);
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
