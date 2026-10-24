using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class ControllersPage : Page
{
    private int _step;
    private bool _ready;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public ControllersPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ControllersViewModel>();
        _ready = true;
        ControllerSetupExpander.IsExpanded = Environment.GetCommandLineArgs().Contains("--controller-setup", StringComparer.Ordinal);
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(1000) };
        CancellationTokenSource? polling = null;
        _timer.Tick += async (_, _) =>
        {
            if (polling is null || !IsVisible || Window.GetWindow(this) is { IsActive: false }) return;
            var model = (ControllersViewModel)DataContext;
            await model.RefreshNativeControllersAsync(polling.Token);
            _timer.Interval = TimeSpan.FromMilliseconds(_step == 1 && model.Devices.Count > 0 ? 25 : 1000);
        };
        Loaded += async (_, _) =>
        {
            var model = (ControllersViewModel)DataContext;
            model.IsTelemetryActive = _step == 1;
            polling?.Cancel();
            polling?.Dispose();
            polling = new();
            _timer.Interval = TimeSpan.FromMilliseconds(_step == 1 ? 25 : 1000);
            _timer.Start();
            await model.RefreshNativeControllersAsync(polling.Token);
        };
        Unloaded += (_, _) =>
        {
            var model = (ControllersViewModel)DataContext;
            model.IsTelemetryActive = false;
            _timer.Stop();
            polling?.Cancel();
            polling?.Dispose();
            polling = null;
        };
    }

    private void Step_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string value } || !int.TryParse(value, out var step)) return;
        _step = step;
        ((ControllersViewModel)DataContext).IsTelemetryActive = IsLoaded && step == 1;
        ConnectPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        TestPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        CustomizePanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        _timer.Interval = TimeSpan.FromMilliseconds(_step == 1 ? 25 : 1000);
        ControllerScroll.ScrollToTop();
    }

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: "1" }) TestStep.IsChecked = true;
        else CustomizeStep.IsChecked = true;
        (_step == 1 ? TestStep : CustomizeStep).Focus();
    }
}
