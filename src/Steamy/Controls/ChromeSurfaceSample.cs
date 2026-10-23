using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Controls;

/// <summary>A small sample of the page's top strip, excluding the overlaid navigation.</summary>
internal sealed class ChromeSurfaceSample
{
    private const int Width = 160, Height = 12;
    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private double _sourceWidth;
    private readonly DrawingVisual _visual = new();
    private readonly RenderTargetBitmap _bitmap = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
    internal ChromeSurfaceSample(FrameworkElement page) => Capture(page);
    internal void Capture(FrameworkElement page)
    {
        _sourceWidth = Math.Max(1, page.ActualWidth);
        using (var draw = _visual.RenderOpen())
        {
            if (page is Pages.GameDetailsPage details)
            {
                draw.PushTransform(new ScaleTransform(Width / _sourceWidth, Height / 64d));
                details.DrawChromeSurface(draw, details, new Rect(0, 0, _sourceWidth, 64));
                draw.Pop();
            }
            else
                draw.DrawRectangle(new VisualBrush(page) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, _sourceWidth, 64), Stretch = Stretch.Fill }, null, new Rect(0, 0, Width, Height));
        }
        try { _bitmap.Clear(); _bitmap.Render(_visual); _bitmap.CopyPixels(_pixels, Width * 4, 0); }
        finally
        {
            // Keep the small render buffer, never the sampled page and its visual tree.
            using var clear = _visual.RenderOpen();
        }
    }
    internal double Luminance(Rect region)
    {
        double sum = 0;
        for (var y = 0; y < 3; y++) for (var x = 0; x < 7; x++)
        {
            var px = Math.Clamp((int)((region.X + region.Width * (x + .5) / 7) / _sourceWidth * Width), 0, Width - 1);
            var py = Math.Clamp((int)((region.Y + region.Height * (y + .5) / 3) / 64 * Height), 0, Height - 1);
            var i = (py * Width + px) * 4;
            static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            sum += .2126 * Linear(_pixels[i + 2]) + .7152 * Linear(_pixels[i + 1]) + .0722 * Linear(_pixels[i]);
        }
        return sum / 21;
    }
}
