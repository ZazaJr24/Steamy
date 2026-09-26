using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class GoldbergPage : Page
{
    public GoldbergPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GoldbergViewModel>();
    }
}
