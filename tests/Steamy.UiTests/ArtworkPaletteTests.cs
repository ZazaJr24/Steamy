using System.Windows.Media;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ArtworkPaletteTests
{
    [Fact]
    public void SpatialAmbientKeepsWhiteLeftAndRedRightSeparate()
    {
        var spatial = ArtworkAmbient.FromPixels(Pixels(96, 54, (x, _) => x < 48 ? Colors.White : Color.FromRgb(220, 25, 35)), 96, 54);
        var brush = Assert.IsType<DrawingBrush>(spatial.Gradient(false));
        Assert.True(brush.IsFrozen);
        Assert.Same(brush, spatial.Gradient(false));
        var drawing = Assert.IsType<DrawingGroup>(brush.Drawing);
        Assert.Equal(24, drawing.Children.Count);
        var left = Assert.IsType<RadialGradientBrush>(Assert.IsType<GeometryDrawing>(drawing.Children[0]).Brush);
        var right = Assert.IsType<RadialGradientBrush>(Assert.IsType<GeometryDrawing>(drawing.Children[7]).Brush);
        Assert.True(left.GradientStops[0].Color.R < 30 && left.GradientStops[0].Color.G < 30 && left.GradientStops[0].Color.B < 35,
            "A pale source under the dark logo scrim must stay dark in the continuation.");
        Assert.True(right.GradientStops[0].Color.R > right.GradientStops[0].Color.G * 4);
        Assert.True(left.Center.X < .1 && right.Center.X > .9);
        Assert.Equal(0, right.GradientStops[^1].Color.A);
    }

    [Fact]
    public void RegionalColorsFollowArtworkInsteadOfAFixedOrangeRedPalette()
    {
        var pixels = Pixels(24, 12, (x, _) => x < 12 ? Color.FromRgb(215, 106, 30) : Color.FromRgb(35, 99, 205));
        var palette = ArtworkPalette.FromPixels(pixels, 24, 12);
        Assert.True(palette.Left.R > palette.Left.B * 3);
        Assert.True(palette.Right.B > palette.Right.R * 3);
        var green = ArtworkPalette.FromPixels(Pixels(24, 12, (_, _) => Color.FromRgb(30, 185, 83)), 24, 12);
        Assert.True(green.Left.G > green.Left.R * 3);
        Assert.True(green.Right.G > green.Right.B * 2);
    }

    [Fact]
    public void DarkBackgroundDoesNotBuryVibrantRegionalAccents()
    {
        var palette = ArtworkPalette.FromPixels(Pixels(24, 12, (x, _) => x < 6 ? Color.FromRgb(235, 113, 28)
            : x >= 18 ? Color.FromRgb(35, 125, 245) : Color.FromRgb(28, 14, 46)), 24, 12);
        Assert.True(palette.Left.R > palette.Left.B * 3);
        Assert.True(palette.Right.B > palette.Right.R * 3);
    }

    [Fact]
    public void TransparentAndWhiteMarginsDoNotOverrideColoredArtwork()
    {
        var palette = ArtworkPalette.FromPixels(Pixels(24, 12, (x, y) => y < 5 ? Colors.Transparent : x < 16 ? Colors.White : Color.FromRgb(153, 46, 187)), 24, 12);
        Assert.True(palette.Left.B > palette.Left.G * 3);
        Assert.True(palette.Right.B > palette.Right.G * 3);
    }

    [Fact]
    public void MonochromeAndMissingArtworkHaveNeutralFallbacks()
    {
        var gray = ArtworkPalette.FromPixels(Pixels(8, 8, (_, _) => Color.FromRgb(110, 110, 110)), 8, 8);
        Assert.Equal(gray.Left.R, gray.Left.G);
        Assert.Equal(gray.Right.G, gray.Right.B);
        Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.FromPixels(new byte[8 * 8 * 4], 8, 8));
        Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.FromImage(null));
    }

    private static byte[] Pixels(int width, int height, Func<int, int, Color> color)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = color(x, y); var offset = (y * width + x) * 4;
            result[offset] = (byte)(pixel.B * pixel.A / 255);
            result[offset + 1] = (byte)(pixel.G * pixel.A / 255);
            result[offset + 2] = (byte)(pixel.R * pixel.A / 255);
            result[offset + 3] = pixel.A;
        }
        return result;
    }
}
