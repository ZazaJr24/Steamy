using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class GoldbergPage : Page
{
    public GoldbergPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GoldbergViewModel>();
    }
}
