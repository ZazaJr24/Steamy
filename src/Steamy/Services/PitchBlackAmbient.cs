using System.Windows;
using System.Windows.Media;

namespace Steamy.Services;

/// <summary>Frozen, inexpensive scene light over an opaque black foundation.</summary>
internal static class PitchBlackAmbient
{
    internal static DrawingBrush Create(ArtworkPalette palette)
    {
        var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, area));
        foreach (var (color, center, alpha) in new[]
        {
            (palette.Left, new Point(.08, .08), (byte)28),
            (palette.Right, new Point(.94, .24), (byte)24)
        })
        {
            var glow = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = .72, RadiusY = .72 };
            glow.GradientStops.Add(new(Color.FromArgb(alpha, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new(Color.FromArgb((byte)(alpha / 4), color.R, color.G, color.B), .55));
            glow.GradientStops.Add(new(Color.FromArgb(0, color.R, color.G, color.B), 1));
            group.Children.Add(new GeometryDrawing(glow, null, area));
        }
        var fade = new LinearGradientBrush { StartPoint = new(0, .35), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new(Color.FromArgb(0, 0, 0, 0), 0));
        fade.GradientStops.Add(new(Colors.Black, 1));
        group.Children.Add(new GeometryDrawing(fade, null, area));
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }
}
