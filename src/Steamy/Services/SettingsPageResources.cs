using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Steamy.Services;

/// <summary>Share immutable page styles on each UI dispatcher, without retaining page instances.</summary>
internal static class SettingsPageResources
{
    private static readonly ConditionalWeakTable<Dispatcher, ResourceDictionary> Dictionaries = new();
    internal static ResourceDictionary Current => Dictionaries.GetValue(Dispatcher.CurrentDispatcher,
        static _ => new ResourceDictionary { Source = new Uri("/Steamy;component/Resources/SettingsStyles.xaml", UriKind.Relative) });
}
