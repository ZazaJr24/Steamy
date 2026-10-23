using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>Pixel-scrolling tile rows. Realizes the viewport plus one row in the scrolling direction.</summary>
public sealed class VirtualizingGamePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty MinItemWidthProperty = LayoutProperty(nameof(MinItemWidth), 210d);
    public static readonly DependencyProperty SpacingProperty = LayoutProperty(nameof(Spacing), 22d);
    public static readonly DependencyProperty RowSpacingProperty = LayoutProperty(nameof(RowSpacing), double.NaN);
    public static readonly DependencyProperty LeadingHeightProperty = LayoutProperty(nameof(LeadingHeight), 0d);
    public static readonly DependencyProperty TrailingHeightProperty = LayoutProperty(nameof(TrailingHeight), 0d);
    public static readonly DependencyProperty CoverRatioProperty = LayoutProperty(nameof(CoverRatio), 0d);
    public static readonly DependencyProperty FooterHeightProperty = LayoutProperty(nameof(FooterHeight), 0d);
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(VirtualizingGamePanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));
    private static DependencyProperty LayoutProperty(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(VirtualizingGamePanel),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double LeadingHeight { get => (double)GetValue(LeadingHeightProperty); set => SetValue(LeadingHeightProperty, value); }
    public double TrailingHeight { get => (double)GetValue(TrailingHeightProperty); set => SetValue(TrailingHeightProperty, value); }
    public double CoverRatio { get => (double)GetValue(CoverRatioProperty); set => SetValue(CoverRatioProperty, value); }
    public double FooterHeight { get => (double)GetValue(FooterHeightProperty); set => SetValue(FooterHeightProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    private double _rowHeight = 104;
    private double RowGap => double.IsNaN(RowSpacing) ? Spacing : Math.Max(0, RowSpacing);
    private double RowPitch => _rowHeight + RowGap;
    private Size _extent, _viewport;
    private double _offset;
    private double _arrangedOffset;
    private readonly TranslateTransform _scrollTranslation = new();
    public VirtualizingGamePanel() => RenderTransform = _scrollTranslation;
    private bool CanTranslate => ReferenceEquals(RenderTransform, _scrollTranslation) && !ClipToBounds && Background is null;
    private bool _backwards;
    private int _columns = 1;
    private int _firstRealized = -1, _lastRealized = -1;
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer ScrollOwner { get; set; } = null!;
    public int RealizedCount => InternalChildren.Count;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        var width = double.IsInfinity(availableSize.Width) ? 900 : Math.Max(1, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? 500 : Math.Max(1, availableSize.Height);
        _columns = Math.Clamp((int)((width + Spacing) / (Math.Max(1, MinItemWidth) + Spacing)), 1, Math.Max(1, MaxColumns));
        var itemWidth = Math.Max(1, (width - Spacing * (_columns - 1)) / _columns);
        _rowHeight = CoverRatio > 0 ? Math.Round(itemWidth * CoverRatio + FooterHeight) : 104;
        var rows = (owner.Items.Count + _columns - 1) / _columns;
        _viewport = new(width, height);
        _extent = new(width, Math.Max(0, LeadingHeight) + Math.Max(0, rows * RowPitch - RowGap) + Math.Max(0, TrailingHeight));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();
        var (first, last) = RealizationRange(owner.Items.Count);
        // Accessing InternalChildren connects an ItemsHost to its generator on the first measure.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return _viewport;
        var recycling = GetVirtualizationMode(owner) == VirtualizationMode.Recycling
            ? generator as IRecyclingItemContainerGenerator : null;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            var child = InternalChildren[i];
            // Keep active input/context-menu state attached to its original item.
            // Own UIElement items are not interchangeable generated containers.
            if (recycling is not null && index >= 0 && index < owner.Items.Count && !ReferenceEquals(child, owner.Items[index]) && CanRecycle(child))
                recycling.Recycle(position, 1);
            else generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                for (var item = first; item <= last; item++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var fresh);
                    if (fresh || !InternalChildren.Contains(child))
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(itemWidth, _rowHeight));
                }
            }
        }
        _firstRealized = first; _lastRealized = last;
        return _viewport;
    }

    private (int First, int Last) RealizationRange(int itemCount)
    {
        var first = Math.Max(0, (int)(Math.Max(0, _offset - LeadingHeight) / RowPitch) - (_backwards ? 1 : 0)) * _columns;
        // The header and footer occupy scroll space without creating dummy game items.
        first = Math.Min(first, Math.Max(0, (itemCount - 1) / _columns * _columns));
        var last = Math.Min(itemCount - 1, ((int)(Math.Max(0, _offset + _viewport.Height - LeadingHeight) / RowPitch) + (_backwards ? 1 : 2)) * _columns - 1);
        return (first, last);
    }

    private static bool CanRecycle(UIElement child)
    {
        if (child.IsKeyboardFocusWithin || child.IsMouseCaptureWithin || child.IsStylusCaptureWithin || child.AreAnyTouchesCapturedWithin) return false;
        return !HasOpenContextMenu(child);
    }
    private static bool HasOpenContextMenu(DependencyObject view)
    {
        if (ContextMenuService.GetContextMenu(view) is { IsOpen: true }) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
            if (HasOpenContextMenu(VisualTreeHelper.GetChild(view, i))) return true;
        return false;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _arrangedOffset = _offset;
        _scrollTranslation.Y = 0;
        var width = Math.Max(1, (finalSize.Width - Spacing * (_columns - 1)) / _columns);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % _columns * (width + Spacing), LeadingHeight + index / _columns * RowPitch - _offset, width, _rowHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && InternalChildren.Count > 0)
            RemoveInternalChildRange(0, InternalChildren.Count);
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or System.Collections.Specialized.NotifyCollectionChangedAction.Replace or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            // A moved item was detached by the generator at its old visual position.
            // Using its destination leaves the visual and generator maps out of sync.
            var position = args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move ? args.OldPosition : args.Position;
            var index = Math.Clamp(position.Index + (position.Offset > 0 ? 1 : 0), 0, InternalChildren.Count);
            var count = Math.Min(args.ItemUICount, InternalChildren.Count - index);
            if (count > 0) RemoveInternalChildRange(index, count);
        }
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index) => SetVerticalOffset(LeadingHeight + index / _columns * RowPitch);
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as DependencyObject;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        var owner = ItemsControl.GetItemsOwner(this);
        if (child is not null && owner is not null)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            var top = LeadingHeight + index / _columns * RowPitch;
            if (top < _offset) SetVerticalOffset(top);
            else if (top + _rowHeight > _offset + _viewport.Height) SetVerticalOffset(top + _rowHeight - _viewport.Height);
        }
        return rectangle;
    }
    public void SetVerticalOffset(double offset)
    {
        var next = Math.Clamp(double.IsNaN(offset) ? 0 : offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(next - _offset) < 0.1) return;
        _backwards = next < _offset; _offset = next;
        var owner = ItemsControl.GetItemsOwner(this);
        var range = RealizationRange(owner?.Items.Count ?? 0);
        // Pixel scrolling inside the same realization window only moves existing
        // tiles. Regenerate/measure when its rows or layout actually change.
        if (IsMeasureValid && range.First == _firstRealized && range.Last == _lastRealized)
        {
            // ScrollContentPresenter owns the stationary viewport clip. Within
            // retained rows, move the vector visuals together on the compositor
            // instead of arranging every tile and its descendants every frame.
            // Keep custom transforms/backgrounds/clips on the normal layout path.
            if (CanTranslate && IsArrangeValid) _scrollTranslation.Y = _arrangedOffset - _offset;
            else InvalidateArrange();
        }
        else InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset - 32);
    public void LineDown() => SetVerticalOffset(_offset + 32);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 96);
    public void MouseWheelDown() => SetVerticalOffset(_offset + 96);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}

public sealed class GameShortcutStrip : Panel
{
    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(nameof(ViewportWidth), typeof(double), typeof(GameShortcutStrip), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ViewportWidth { get => (double)GetValue(ViewportWidthProperty); set => SetValue(ViewportWidthProperty, value); }
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(GameShortcutStrip), new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0, width = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            width += Math.Max(TileWidth, child.DesiredSize.Width) + 22;
        }
        return new Size(Math.Max(ViewportWidth, Math.Max(0, width - 22)), height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var occupied = InternalChildren.Cast<UIElement>().Sum(child => Math.Max(TileWidth, child.DesiredSize.Width));
        var gap = InternalChildren.Count > 1 ? Math.Max(22, (finalSize.Width - occupied) / (InternalChildren.Count - 1)) : 0;
        foreach (UIElement child in InternalChildren)
        {
            var width = Math.Max(TileWidth, child.DesiredSize.Width);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + gap;
        }
        return finalSize;
    }
}
