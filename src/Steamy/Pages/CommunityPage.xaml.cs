using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class CommunityPage : Page
{
    private CancellationTokenSource? _loading;
    public CommunityPage()
    {
        InitializeComponent(); var model = App.Services.GetRequiredService<CommunityViewModel>(); DataContext = model;
        Loaded += async (_, _) => { _loading?.Cancel(); _loading?.Dispose(); var request = _loading = new(); await model.LoadAsync(request.Token); };
        Unloaded += (_, _) => _loading?.Cancel();
    }
    private void Friends_Click(object sender, RoutedEventArgs args) => App.Services.GetRequiredService<INavigationService>().Navigate<FriendsPage>();
}
