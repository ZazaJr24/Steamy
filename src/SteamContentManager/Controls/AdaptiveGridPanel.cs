using System.Windows;
using System.Windows.Controls;

namespace SteamContentManager.Controls;

// Grid that fits as many columns as MinItemWidth allows and stretches them to fill the row,
// so the gallery has no ragged gap on the right at any window width.
public sealed class AdaptiveGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CoverRatioProperty = DependencyProperty.Register(
        nameof(CoverRatio), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FooterHeightProperty = DependencyProperty.Register(
        nameof(FooterHeight), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double CoverRatio { get => (double)GetValue(CoverRatioProperty); set => SetValue(CoverRatioProperty, value); }
    public double FooterHeight { get => (double)GetValue(FooterHeightProperty); set => SetValue(FooterHeightProperty, value); }

    private (int Columns, double ItemWidth, double ItemHeight) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 4 + Spacing * 3;
        var columns = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        var itemWidth = Math.Floor((width - Spacing * (columns - 1)) / columns);
        return (columns, itemWidth, Math.Round(itemWidth * CoverRatio + FooterHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, w, h) = Layout(availableSize.Width);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(w, h));
        var rows = (InternalChildren.Count + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? columns * w + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, rows == 0 ? 0 : rows * h + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, w, h) = Layout(finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            InternalChildren[i].Arrange(new Rect(col * (w + Spacing), row * (h + Spacing), w, h));
        }
        return finalSize;
    }
}
