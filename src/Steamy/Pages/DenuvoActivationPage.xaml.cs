using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DenuvoActivationPage : Page
{
    public DenuvoActivationPage()
    {
        Resources.Add("CountVis", new CountToVisibilityConverter());
        Resources.Add("StringVis", new StringToVisibilityConverter());
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DenuvoActivationViewModel>();
        Loaded += (_, _) => _ = ViewModel.EnsureToolDownloadedAsync();
    }

    private DenuvoActivationViewModel ViewModel => (DenuvoActivationViewModel)DataContext;

    private void Suggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is SteamSearchEntry entry)
            ViewModel.SelectSuggestion(entry);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.GenerateCommand.CanExecute(null))
            ViewModel.GenerateCommand.Execute(null);
    }
}

internal sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
