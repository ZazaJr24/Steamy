using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class DenuvoActivationPage : Page
{
    public DenuvoActivationPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DenuvoActivationViewModel>();

        // Startet den Tool-Download erst nach dem Laden der Page, damit der Konstruktor
        // nicht während eines WebRequests hängt.
        Loaded += (_, _) => _ = ViewModel.EnsureToolDownloadedAsync();
    }

    private DenuvoActivationViewModel ViewModel => (DenuvoActivationViewModel)DataContext;
}
