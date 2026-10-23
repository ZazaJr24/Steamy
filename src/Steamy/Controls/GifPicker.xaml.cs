using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Controls;

public partial class GifPicker : UserControl
{
    public GifPicker()
    {
        InitializeComponent(); DataContext = App.Services.GetRequiredService<GifPickerViewModel>();
        Loaded += async (_, _) => await ((GifPickerViewModel)DataContext).OpenAsync();
        Unloaded += (_, _) => ((GifPickerViewModel)DataContext).Suspend();
    }
}
