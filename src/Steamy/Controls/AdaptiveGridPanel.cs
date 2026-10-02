using System.Windows;
using System.Windows.Controls;

namespace Steamy.Controls;

// Responsive gallery with optional column and cover-width limits.
public sealed class AdaptiveGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxItemWidth { get => (double)GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CoverRatioProperty = DependencyProperty.Register(
        nameof(CoverRatio), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FooterHeightProperty = DependencyProperty.Register(
        nameof(FooterHeight), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Shows at most this many full rows (0 = all). Extra children are not arranged, so a
    /// preview grid never ends in a half-filled row whatever the window width.</summary>
    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
        nameof(MaxRows), typeof(int), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public int MaxRows { get => (int)GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }
    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double CoverRatio { get => (double)GetValue(CoverRatioProperty); set => SetValue(CoverRatioProperty, value); }
    public double FooterHeight { get => (double)GetValue(FooterHeightProperty); set => SetValue(FooterHeightProperty, value); }

    private (int Columns, double ItemWidth, double ItemHeight) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 4 + Spacing * 3;
        var columns = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        if (MaxColumns > 0) columns = Math.Min(columns, MaxColumns);
        var itemWidth = Math.Max(1, Math.Min(MaxItemWidth, Math.Floor((width - Spacing * (columns - 1)) / columns)));
        return (columns, itemWidth, Math.Round(itemWidth * CoverRatio + FooterHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, w, h) = Layout(availableSize.Width);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(w, h));
        var rows = (VisibleCount(columns) + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? columns * w + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, rows == 0 ? 0 : rows * h + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, w, h) = Layout(finalSize.Width);
        var visible = VisibleCount(columns);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (i >= visible)
            {
                InternalChildren[i].Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var col = i % columns;
            var row = i / columns;
            InternalChildren[i].Arrange(new Rect(col * (w + Spacing), row * (h + Spacing), w, h));
        }
        return finalSize;
    }

    private int VisibleCount(int columns)
    {
        var count = InternalChildren.Count;
        if (MaxRows <= 0 || count <= columns) return count;
        return Math.Min(MaxRows, count / columns) * columns;
    }
}
