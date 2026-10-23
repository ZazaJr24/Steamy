using System.Windows;
using Wpf.Ui.Controls;

namespace Steamy.Controls;

/// <summary>
/// WPF-UI 4.2 also keeps page instances in its header-association dictionary.
/// Release an evicted page there as well as in our page provider.
/// </summary>
public sealed class BoundedNavigationView : NavigationView
{
    internal void ReleasePage(FrameworkElement page) => PageToNavigationItemDictionary.Remove(page);
    internal int AssociatedPageCount => PageToNavigationItemDictionary.Count;
}
