using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Wpf.Ui.Abstractions;

namespace Steamy.Services;

/// <summary>Creates WPF pages requested by WPF-UI's NavigationView.</summary>
public sealed class NavigationViewPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        if (!typeof(FrameworkElement).IsAssignableFrom(pageType)) return null;

        return ActivatorUtilities.CreateInstance(services, pageType);
    }
}
