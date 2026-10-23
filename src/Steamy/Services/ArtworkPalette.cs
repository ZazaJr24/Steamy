using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>Small, cached regional color samples; never samples the live window or every scroll frame.</summary>
internal sealed record ArtworkPalette(Color Left, Color Right)
{
    private static readonly ConditionalWeakTable<ImageSource, ArtworkPalette> Cache = new();
    internal static readonly ArtworkPalette Neutral = new(Color.FromRgb(62, 77, 96), Color.FromRgb(67, 65, 83));

    public static ArtworkPalette FromImage(ImageSource? source) => source is null ? Neutral : Cache.GetValue(source, Sample);

    internal static Task<ArtworkPalette> FromImageAsync(ImageSource? source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source is null) return Task.FromResult(Neutral);
        if (Cache.TryGetValue(source, out var cached)) return Task.FromResult(cached);
        // WPF drawing/readback belongs to the existing STA worker. A frozen
        // snapshot is safe there; the weak key still belongs to the caller's image.
        var frozen = source.IsFrozen ? source : (ImageSource)source.GetCurrentValueAsFrozen();
        return ArtworkRenderWorker.RunAsync(() => Cache.GetValue(source, _ => Sample(frozen)), token);
    }

    private static ArtworkPalette Sample(ImageSource source)
    {
        const int width = 96, height = 54;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return FromPixels(pixels, width, height);
    }

    internal static ArtworkPalette FromPixels(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Expected a premultiplied BGRA image.", nameof(pixels));
        Color Pick(bool left)
        {
            var bins = new (double Weight, double R, double G, double B)[24];
            double grayWeight = 0, grayR = 0, grayG = 0, grayB = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var alpha = pixels[offset + 3] / 255d;
                if (alpha < .7) continue; // Transparent artwork margins must not become black accents.
                var r = Math.Min(255, pixels[offset + 2] / alpha);
                var g = Math.Min(255, pixels[offset + 1] / alpha);
                var b = Math.Min(255, pixels[offset] / alpha);
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max < 30 || min > 225) continue;
                var saturation = (max - min) / max;
                var region = left ? 1 - x / (double)width : x / (double)width;
                var weight = (.02 + Math.Pow(region, 4)) * (.75 + y / (double)height * .5) * alpha;
                grayWeight += weight; grayR += r * weight; grayG += g * weight; grayB += b * weight;
                if (saturation < .16) continue;
                var delta = max - min;
                var hue = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
                hue = (hue * 60 + 360) % 360;
                var index = Math.Min(23, (int)(hue / 15));
                // Broad, almost-black backgrounds should not bury the artwork's lit accents.
                weight *= (.3 + saturation) * Math.Pow(max / 255d, 2);
                var bin = bins[index];
                bins[index] = (bin.Weight + weight, bin.R + r * weight, bin.G + g * weight, bin.B + b * weight);
            }
            var winner = bins.MaxBy(bin => bin.Weight);
            if (winner.Weight > 0)
                return Color.FromRgb((byte)(winner.R / winner.Weight), (byte)(winner.G / winner.Weight), (byte)(winner.B / winner.Weight));
            return grayWeight > 0
                ? Color.FromRgb((byte)(grayR / grayWeight), (byte)(grayG / grayWeight), (byte)(grayB / grayWeight))
                : left ? Neutral.Left : Neutral.Right;
        }
        return new(Pick(true), Pick(false));
    }

    public Brush Background(bool light)
    {
        var neutral = light ? Color.FromRgb(245, 247, 250) : Color.FromRgb(15, 19, 26);
        var average = Color.FromRgb((byte)((Left.R + Right.R) / 2), (byte)((Left.G + Right.G) / 2), (byte)((Left.B + Right.B) / 2));
        var amount = light ? .07 : .12;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(neutral.R * (1 - amount) + average.R * amount),
            (byte)(neutral.G * (1 - amount) + average.G * amount),
            (byte)(neutral.B * (1 - amount) + average.B * amount)));
        brush.Freeze(); return brush;
    }

    public Brush Gradient(bool light)
    {
        var drawing = new DrawingGroup();
        foreach (var (color, center) in new[] { (Left, new Point(.12, .12)), (Right, new Point(.92, .18)) })
        {
            var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = .85, RadiusY = 1 };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)45 : (byte)95, color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            drawing.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }
        var result = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        result.Freeze(); return result;
    }
}
