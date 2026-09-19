using System.Globalization;
using System.Windows.Data;

namespace SteamContentManager;

/// <summary>
/// WPF-UI 4.2 ships an EnumToBoolConverter whose Convert back-ends into an ArgumentException when
/// the binding first activates. The library page uses this converter so the NSFW scope checkbox
/// can bind directly without surprising the application at startup.
/// </summary>
public sealed class EnumToShowBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return false;
        var enumValue = value.ToString();
        var parameterString = parameter is string s ? s : null;
        return enumValue is not null && parameterString is not null
            && string.Equals(enumValue, parameterString, StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
