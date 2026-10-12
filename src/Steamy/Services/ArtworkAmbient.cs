using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>Preserves the spatial colors, including pale and gray regions, of banner artwork.</summary>
internal sealed class ArtworkAmbient
{
    private const int Columns = 8, Rows = 3;
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<int, ArtworkAmbient>> Cache = new();
    private readonly Color[] _regions;
    private Brush? _dark, _light;
    private Brush? _continuousDark, _continuousLight;
    private ArtworkAmbient(Color[] regions) => _regions = regions;

    internal static ArtworkAmbient FromImage(ImageSource source, double aspect = 16d / 9)
    {
        var key = (int)Math.Round(Math.Clamp(aspect, .5, 6) * 16);
        var versions = Cache.GetOrCreateValue(source);
        if (versions.TryGetValue(key, out var cached)) return cached;
        if (versions.Count >= 8) versions.Remove(versions.Keys.First());
        return versions[key] = Sample(source, key / 16d);
    }
    private static ArtworkAmbient Sample(ImageSource source, double aspect)
    {
        const int width = 96;
        var height = Math.Clamp((int)Math.Round(width / aspect), 16, 192);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new ImageBrush(source) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return FromPixels(pixels, width, height);
    }

    internal static ArtworkAmbient FromPixels(byte[] pixels, int width, int height)
    {
        if (width < Columns || height < Rows || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Expected a premultiplied BGRA banner.", nameof(pixels));
        var regions = new Color[Columns * Rows];
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            double r = 0, g = 0, b = 0, weight = 0;
            // Continue the middle and lower artwork into the body, retaining each region's location.
            var top = height * (row + 1) / (Rows + 1);
            var bottom = Math.Min(height, height * (row + 2) / (Rows + 1));
            for (var y = top; y < bottom; y++)
            for (var x = column * width / Columns; x < (column + 1) * width / Columns; x++)
            {
                var offset = (y * width + x) * 4;
                var alpha = pixels[offset + 3] / 255d;
                if (alpha < .1) continue;
                // White belongs to a white region; never replace it with a distant red accent.
                r += pixels[offset + 2]; g += pixels[offset + 1]; b += pixels[offset]; weight += alpha;
            }
            regions[row * Columns + column] = weight > 0
                ? Color.FromRgb((byte)Math.Clamp(r / weight, 0, 255), (byte)Math.Clamp(g / weight, 0, 255), (byte)Math.Clamp(b / weight, 0, 255))
                : Color.FromRgb(24, 29, 37);
        }
        return new(regions);
    }

    // Full-page ambient: broad overlapping regions retain artwork colors at both ends
    // of the page. Frozen brushes avoid live blur, scroll sampling and animation loops.
    internal Brush ContinuousGradient(bool light)
    {
        var cached = light ? _continuousLight : _continuousDark;
        if (cached is not null) return cached;
        var drawing = new DrawingGroup();
        var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            var color = _regions[row * Columns + column];
            var scale = light ? 1d : Math.Min(1, 100d / Math.Max(1, (int)Math.Max(color.R, Math.Max(color.G, color.B))));
            var center = new Point((column + .5) / Columns, row / (double)(Rows - 1));
            var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = .48, RadiusY = .85 };
            var alpha = light ? (byte)35 : (byte)72;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, (byte)(color.R * scale), (byte)(color.G * scale), (byte)(color.B * scale)), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * .65), (byte)(color.R * scale), (byte)(color.G * scale), (byte)(color.B * scale)), .45));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            drawing.Children.Add(new GeometryDrawing(brush, null, area));
        }
        var result = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        result.Freeze();
        if (light) _continuousLight = result; else _continuousDark = result;
        return result;
    }

    internal Brush Gradient(bool light)
    {
        var cached = light ? _light : _dark;
        if (cached is not null) return cached;
        var drawing = new DrawingGroup();
        var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            var color = _regions[row * Columns + column];
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var scale = light ? 1d : Math.Min(1, 110d / Math.Max(1, (int)maximum));
            color = Color.FromRgb((byte)(color.R * scale), (byte)(color.G * scale), (byte)(color.B * scale));
            // The visible banner includes a dark logo scrim. Continue that same scrim,
            // rather than reintroducing the unshaded pale source below the logo.
            var x = (column + .5) / Columns;
            var shade = x <= .3 ? .93 + (.81 - .93) * x / .3
                : x <= .62 ? .81 + (.31 - .81) * (x - .3) / .32
                : .31 * (1 - x) / .38;
            color = Color.FromRgb((byte)(color.R * (1 - shade) + 8 * shade),
                (byte)(color.G * (1 - shade) + 12 * shade), (byte)(color.B * (1 - shade) + 19 * shade));
            var center = new Point((column + .5) / Columns, .06 + row * .09);
            var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = .24, RadiusY = .32 };
            var alpha = light ? (byte)45 : (byte)100;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * .65), color.R, color.G, color.B), .4));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            drawing.Children.Add(new GeometryDrawing(brush, null, area));
        }
        var result = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        result.Freeze();
        if (light) _light = result; else _dark = result;
        return result;
    }
}
