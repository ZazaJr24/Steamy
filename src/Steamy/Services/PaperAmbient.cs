using System.Windows;
using System.Windows.Media;

namespace Steamy.Services;

/// <summary>A white canvas with restrained artwork color; no live blur or animation.</summary>
internal static class PaperAmbient
{
    internal static DrawingBrush Create(ArtworkPalette palette)
    {
        var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, area));
        foreach (var (color, center) in new[] { (palette.Left, new Point(.06, .04)), (palette.Right, new Point(.96, .16)) })
        {
            var glow = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = .75, RadiusY = .65 };
            glow.GradientStops.Add(new(Color.FromArgb(12, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new(Color.FromArgb(0, color.R, color.G, color.B), 1));
            drawing.Children.Add(new GeometryDrawing(glow, null, area));
        }
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill }; brush.Freeze(); return brush;
    }
}
