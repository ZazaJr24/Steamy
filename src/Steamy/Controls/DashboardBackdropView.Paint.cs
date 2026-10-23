using System.Windows;
using System.Windows.Media;
using Steamy.Services;

namespace Steamy.Controls;

public sealed partial class DashboardBackdropView
{
    private FramePaint? _framePaint;
    private ArtworkPaint? _currentPaint, _previousPaint;
    private PreviewPaint? _previewPaint;

    private void ClearFramePaint()
    { _framePaint = null; _currentPaint = null; _previousPaint = null; _previewPaint = null; }

    private PreviewPaint GetPreviewPaint(Rect body)
    {
        if (_previewPaint is null || _previewPaint.Bounds != body || !ReferenceEquals(_previewPaint.Source, _previewBrush)
            || !ReferenceEquals(_previewPaint.PreviousSource, _previousPreviewBrush))
            _previewPaint = new PreviewPaint(body, _previewBrush!, _previousPreviewBrush);
        return _previewPaint;
    }

    private FramePaint GetFramePaint()
    {
        if (_framePaint is not { } paint || paint.Size != RenderSize || paint.HeroHeight != HeroHeight
            || paint.Light != UiThemeService.IsLight || paint.AmbientEnabled != UiThemeService.AmbientEnabled)
        {
            ClearFramePaint();
            _framePaint = new FramePaint(RenderSize, HeroHeight, UiThemeService.IsLight, UiThemeService.AmbientEnabled);
        }
        return _framePaint!;
    }

    private ArtworkPaint GetArtworkPaint(ImageSource artwork, ImageSource? continuation, FramePaint paint)
    {
        ref var slot = ref (ReferenceEquals(artwork, _previous) ? ref _previousPaint : ref _currentPaint);
        if (slot is null || !ReferenceEquals(slot.Hero.ImageSource, artwork) || !ReferenceEquals(slot.Ambient?.ImageSource, continuation))
            slot = new ArtworkPaint(artwork, continuation, paint);
        return slot;
    }

    // Only small frozen brushes are retained. Each recording still crops its
    // rectangles/masks to the viewport; no full-page raster cache is introduced.
    private sealed class FramePaint
    {
        internal Size Size { get; }
        internal double HeroHeight { get; }
        internal bool Light { get; }
        internal bool AmbientEnabled { get; }
        internal Rect HeroBounds { get; }
        internal Rect AmbientBounds { get; }
        internal Rect ScrimBounds { get; }
        internal Brush BodyFade { get; }
        internal Brush HeroFade { get; }
        internal Brush Scrim { get; }
        internal Brush ScrimFade { get; }

        internal FramePaint(Size size, double heroHeight, bool light, bool ambientEnabled)
        {
            Size = size; HeroHeight = heroHeight; Light = light; AmbientEnabled = ambientEnabled;
            HeroBounds = new Rect(0, 0, size.Width, heroHeight);
            AmbientBounds = new Rect(0, 0, size.Width, heroHeight * ArtworkContinuation.HeightInHeroes);
            ScrimBounds = !ambientEnabled || light ? HeroBounds : AmbientBounds;
            var end = Math.Min(size.Height * .72, Math.Max(heroHeight * 1.45, size.Height * .60));
            var color = light ? Colors.White : Colors.Black;
            BodyFade = Gradient(new Point(0, Math.Min(end, Math.Max(heroHeight * 1.12, size.Height * .45))), new Point(0, end),
                (0, Color.FromArgb(0, color.R, color.G, color.B)), (.5, Color.FromArgb(160, color.R, color.G, color.B)), (1, color));
            HeroFade = Gradient(new Point(), new Point(0, heroHeight),
                (0, Colors.White), (.4, Colors.White), (.6, Color.FromArgb(210, 255, 255, 255)),
                (.85, Color.FromArgb(45, 255, 255, 255)), (1, Colors.Transparent));
            Scrim = Gradient(new Point(), new Point(size.Width, 0),
                (0, Color.FromArgb(215, 0, 0, 0)), (.3, Color.FromArgb(180, 0, 0, 0)),
                (.65, Color.FromArgb(45, 0, 0, 0)), (1, Color.FromArgb(0, 0, 0, 0)));
            ScrimFade = Gradient(new Point(), new Point(0, ScrimBounds.Height),
                (0, Colors.White), (.22, Colors.White), (.45, Color.FromArgb(130, 255, 255, 255)), (1, Colors.Transparent));
        }
        private static Brush Gradient(Point start, Point end, params (double Offset, Color Color)[] stops)
        {
            var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
            foreach (var (offset, color) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
            brush.Freeze(); return brush;
        }
    }

    private sealed class ArtworkPaint
    {
        internal ImageBrush Hero { get; }
        internal ImageBrush? Ambient { get; }
        internal ArtworkPaint(ImageSource artwork, ImageSource? continuation, FramePaint paint)
        {
            Hero = Image(artwork, paint.HeroBounds, Stretch.UniformToFill);
            if (continuation is not null) Ambient = Image(continuation, paint.AmbientBounds, Stretch.Fill);
        }
        private static ImageBrush Image(ImageSource image, Rect viewport, Stretch stretch)
        {
            var brush = new ImageBrush(image) { Stretch = stretch, ViewportUnits = BrushMappingMode.Absolute, Viewport = viewport };
            brush.Freeze(); return brush;
        }
    }

    private sealed class PreviewPaint
    {
        internal Rect Bounds { get; }
        internal Brush Source { get; }
        internal Brush? PreviousSource { get; }
        internal Brush Current { get; }
        internal Brush? Previous { get; }
        internal Brush Reveal { get; }
        internal PreviewPaint(Rect bounds, Brush current, Brush? previous)
        {
            Bounds = bounds; Source = current; PreviousSource = previous;
            Current = At(bounds, current);
            if (previous is not null) Previous = At(bounds, previous);
            Reveal = At(bounds, PreviewReveal);
        }
        private static Brush At(Rect bounds, Brush source)
        {
            var brush = new DrawingBrush(new GeometryDrawing(source, null, new RectangleGeometry(new Rect(0, 0, 1, 1))))
                { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds };
            brush.Freeze(); return brush;
        }
    }
}
