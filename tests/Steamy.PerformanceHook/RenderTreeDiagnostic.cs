using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Geometry/resource descriptions only: no text, URLs, image bytes or user content.
internal static class RenderTreeDiagnostic
{
    internal static object Capture(Window window)
    {
        var views = new List<object>();
        var layers = new List<object>();
        var visited = 0;
        static object? Box(Rect box) => box.IsEmpty ? null : new { x = box.X, y = box.Y, width = box.Width, height = box.Height };
        static object? Image(ImageSource? source) => source is BitmapSource bitmap
            ? new { type = source.GetType().Name, width = bitmap.PixelWidth, height = bitmap.PixelHeight, frozen = source.IsFrozen } : null;
        static object? BrushInfo(Brush? brush) => brush switch
        {
            VisualBrush visual => new { type = "VisualBrush", sourceType = visual.Visual?.GetType().Name, viewbox = Box(visual.Viewbox), sourceBounds = visual.Visual is Visual source ? Box(VisualTreeHelper.GetDescendantBounds(source)) : null },
            ImageBrush image => new { type = "ImageBrush", image = Image(image.ImageSource) },
            null => null,
            _ => new { type = brush.GetType().Name }
        };
        void Drawings(Drawing? drawing, int owner, int depth)
        {
            if (drawing is null || layers.Count >= 512 || depth > 24) return;
            if (drawing is DrawingGroup group)
            {
                if (group.OpacityMask is not null || group.Opacity < 1 || group.ClipGeometry is not null)
                    layers.Add(new { owner, depth, type = "DrawingGroup", bounds = Box(group.Bounds), opacity = group.Opacity,
                        mask = BrushInfo(group.OpacityMask), clip = group.ClipGeometry is null ? null : Box(group.ClipGeometry.Bounds) });
                foreach (var child in group.Children) Drawings(child, owner, depth + 1);
            }
            else if (drawing is GeometryDrawing { Brush: ImageBrush or VisualBrush } geometry)
                layers.Add(new { owner, depth, type = "GeometryDrawing", bounds = Box(geometry.Bounds), brush = BrushInfo(geometry.Brush) });
            else if (drawing is ImageDrawing image)
                layers.Add(new { owner, depth, type = "ImageDrawing", bounds = Box(image.Bounds), image = Image(image.ImageSource) });
        }
        void Visit(DependencyObject node)
        {
            if (++visited > 20000) return;
            var type = node.GetType().Name;
            if (node is Visual visual)
            {
                var element = node as FrameworkElement;
                var effect = (node as UIElement)?.Effect;
                var interesting = effect is not null || type is "DashboardBackdropView" or "ShellBackdropView" or "ScrollViewer" or "ScrollContentPresenter"
                    || type == "DrawingVisual" && VisualTreeHelper.GetParent(node)?.GetType().Name == "ShellBackdropView"
                    || node is System.Windows.Controls.Image { Source: not null };
                if (interesting && views.Count < 512)
                {
                    var owner = views.Count;
                    object? screenBounds = null;
                    if (element?.IsVisible == true)
                        try { screenBounds = Box(element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize))); } catch (InvalidOperationException) { }
                    views.Add(new { owner, type, visible = (node as UIElement)?.IsVisible, width = element?.ActualWidth, height = element?.ActualHeight,
                        opacity = (node as UIElement)?.Opacity, effect = effect?.GetType().Name, cache = (node as UIElement)?.CacheMode?.GetType().Name,
                        contentBounds = Box(VisualTreeHelper.GetContentBounds(visual)), descendantBounds = Box(VisualTreeHelper.GetDescendantBounds(visual)), screenBounds,
                        image = node is System.Windows.Controls.Image image ? Image(image.Source) : null });
                    if (type is "DashboardBackdropView" or "ShellBackdropView" or "DrawingVisual") Drawings(VisualTreeHelper.GetDrawing(visual), owner, 0);
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(window);
        return new { scope = "Logical drawing/effect bounds, not measured GPU allocation sizes. No visual mutations or pixel captures. Arrays bounded at 512 entries.", visited, views, layers };
    }
}
