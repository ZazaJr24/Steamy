using System.Globalization;
using System.Windows.Data;

namespace Steamy.Pages;

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

public sealed class GenreNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (string.Equals(s, "All genres", StringComparison.OrdinalIgnoreCase)) return "All";
            return s;
        }
        return value ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class GenreCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string genre || string.Equals(genre, "All genres", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            var library = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Steamy.ViewModels.LibraryViewModel>(App.Services);
            if (library is not null)
            {
                var count = library.GetGenreCount(genre);
                if (count > 0) return count.ToString("N0", culture);
            }
        }
        catch { }
        return "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
