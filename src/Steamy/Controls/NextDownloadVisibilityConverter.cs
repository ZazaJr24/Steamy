using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Steamy.Controls;

public sealed class NextDownloadVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
