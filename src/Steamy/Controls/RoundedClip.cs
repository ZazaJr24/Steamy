using System.Windows;
using System.Windows.Media;

namespace Steamy.Controls;

public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0d, Changed));
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement view) return;
        view.SizeChanged -= Resize;
        view.SizeChanged += Resize;
        Update(view);
    }
    private static void Resize(object sender, SizeChangedEventArgs args) => Update((FrameworkElement)sender);
    private static void Update(FrameworkElement view) => view.Clip = new RectangleGeometry(new Rect(view.RenderSize), GetRadius(view), GetRadius(view));
}
