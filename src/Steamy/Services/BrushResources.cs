using System.Windows;
using System.Windows.Media;

namespace Steamy.Services;

/// <summary>Preserve unchanged materials instead of invalidating every dynamic resource consumer.</summary>
internal static class BrushResources
{
    internal static void Set(ResourceDictionary resources, string key, Brush value)
    {
        if (resources.Contains(key) && resources[key] is Brush current && Equivalent(current, value)) return;
        resources[key] = value;
    }
    internal static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key) && resources[key] is SolidColorBrush { IsFrozen: true, Opacity: 1 } current
            && current.Color == color && current.Transform.Value.IsIdentity && current.RelativeTransform.Value.IsIdentity) return;
        var brush = new SolidColorBrush(color); brush.Freeze();
        resources[key] = brush;
    }
    private static bool Equivalent(Brush current, Brush value) => ReferenceEquals(current, value)
        || current is SolidColorBrush first && value is SolidColorBrush second && first.IsFrozen && second.IsFrozen
        && first.Color == second.Color && first.Opacity == second.Opacity
        && first.Transform.Value.IsIdentity && first.RelativeTransform.Value.IsIdentity
        && second.Transform.Value.IsIdentity && second.RelativeTransform.Value.IsIdentity;
}
