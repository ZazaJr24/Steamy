using System.Windows;

namespace Steamy.Controls;

/// <summary>Selection belongs to the current route, independently of hover and keyboard focus.</summary>
public static class ShellNavigation
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(ShellNavigation), new PropertyMetadata(false));
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
}
