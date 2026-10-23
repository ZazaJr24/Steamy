using System.Windows;
using System.Windows.Media;

namespace Steamy.Services;

internal static class InstallationAmbient
{
    internal static DrawingBrush Create(ArtworkPalette palette, bool light)
    {
        var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(light ? Color.FromArgb(244, 255, 255, 255) : Color.FromArgb(230, 20, 22, 26)), null, area));
        var tint = new LinearGradientBrush(Color.FromArgb(light ? (byte)10 : (byte)42, palette.Left.R, palette.Left.G, palette.Left.B), Color.FromArgb(light ? (byte)7 : (byte)28, palette.Right.R, palette.Right.G, palette.Right.B), 30);
        drawing.Children.Add(new GeometryDrawing(tint, null, area));
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze(); return brush;
    }
}
