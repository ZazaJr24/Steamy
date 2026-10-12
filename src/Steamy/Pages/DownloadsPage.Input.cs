using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DownloadsPage
{
    private Window? _deckWindow;
    private IInputElement? _deckRestoreFocus;
    private readonly DispatcherTimer _controllerTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private ushort _controllerButtons;
    private bool _controllerUnavailable;

    private void AttachDeckInput()
    {
        _deckWindow = Window.GetWindow(this);
        if (_deckWindow is not null) _deckWindow.PreviewKeyDown += DeckKeyDown;
        _controllerTimer.Tick += PollController;
        _controllerTimer.Start();
    }
    private void DetachDeckInput()
    {
        if (_deckWindow is not null) _deckWindow.PreviewKeyDown -= DeckKeyDown;
        _deckWindow = null; _controllerTimer.Stop(); _controllerTimer.Tick -= PollController;
        _controllerButtons = 0;
        DownloadOptionsLayer.Visibility = Visibility.Collapsed;
        FeaturedPanel.IsEnabled = DownloadQueue.IsEnabled = DeckFooter.IsEnabled = true;
    }
    private void SetDownloadOptions(bool open)
    {
        if (open) _deckRestoreFocus = Keyboard.FocusedElement;
        DownloadOptionsLayer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FeaturedPanel.IsEnabled = DownloadQueue.IsEnabled = DeckFooter.IsEnabled = !open;
        if (open) DownloadOptionsClose.Focus();
        else if (_deckRestoreFocus is FrameworkElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        else DownloadOptionsButton.Focus();
    }
    private void DownloadOptions_Click(object sender, RoutedEventArgs e) => SetDownloadOptions(DownloadOptionsLayer.Visibility != Visibility.Visible);
    private void DownloadOptionsDismiss_Click(object sender, MouseButtonEventArgs e) { SetDownloadOptions(false); e.Handled = true; }
    private void DownloadSearch_Click(object sender, RoutedEventArgs e) { SetDownloadOptions(true); DownloadSearch.Focus(); }
    private void DownloadMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            window.RootNavigationView.IsPaneOpen = !window.RootNavigationView.IsPaneOpen;
            if (window.RootNavigationView.IsPaneOpen) window.RootNavigationView.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().FirstOrDefault()?.Focus();
        }
    }
    private void DownloadBack_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadOptionsLayer.IsVisible) SetDownloadOptions(false);
        else if (Window.GetWindow(this) is MainWindow window && window.RootNavigationView.CanGoBack) window.RootNavigationView.GoBack();
        else if (App.Services.GetService(typeof(INavigationService)) is INavigationService navigation) BackToLibrary(navigation);
    }
    private static bool BackToLibrary(INavigationService navigation) { navigation.Navigate<LibraryPage>(); return true; }
    private void DownloadToggle_Click(object sender, RoutedEventArgs e)
    {
        var model = (DownloadsViewModel)DataContext;
        if (model.HeroJob is not { } job) return;
        var command = job.IsPaused ? model.ResumeCommand : model.PauseCommand;
        if (job.IsPaused || job.IsActive) { if (command.CanExecute(job)) command.Execute(job); }
    }
    private void DeckKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !IsVisible || _deckWindow is MainWindow { RootNavigationView.IsPaneOpen: true }) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { DownloadSearch_Click(this, e); e.Handled = true; }
        else if (key == Key.F10) { DownloadOptions_Click(this, e); e.Handled = true; }
        else if (key == Key.Escape) { DownloadBack_Click(this, e); e.Handled = true; }
        else if (key == Key.Space && !DownloadOptionsLayer.IsVisible && Keyboard.FocusedElement is not (TextBoxBase or ButtonBase or ComboBox))
        { DownloadToggle_Click(this, e); e.Handled = true; }
    }
    private void ApplyDeckAccessibility()
    {
        var highContrast = UiThemeService.IsHighContrastActive || SystemParameters.HighContrast;
        Brush ColorBrush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
        Resources["DeckBackground"] = highContrast ? SystemColors.WindowBrush : !UiThemeService.AmbientEnabled ? UiThemeService.BackgroundBrush : ColorBrush("#101319");
        Resources["DeckHeroBackground"] = highContrast ? SystemColors.WindowBrush
            : ColorBrush(!UiThemeService.AmbientEnabled && UiThemeService.StaticBackgroundIsLight ? "#EAF5F5F7" : "#FF171B22");
        foreach (var (key, color) in new[] { ("DeckText", "#F1F2F3"), ("DeckSecondary", "#B8B6B4"), ("DeckMuted", "#89949E") })
            Resources[key] = highContrast ? SystemColors.WindowTextBrush : !UiThemeService.AmbientEnabled && UiThemeService.StaticBackgroundIsLight ? Brushes.Black : ColorBrush(color);
        Resources["DeckBlue"] = highContrast ? SystemColors.HighlightBrush : ColorBrush("#1A9FFF");
        Resources["DeckGreen"] = highContrast ? SystemColors.HighlightBrush : ColorBrush("#A4D007");
        Resources["DeckLine"] = highContrast ? SystemColors.GrayTextBrush : ColorBrush("#3B444D");
        var lightBackground = !UiThemeService.AmbientEnabled && UiThemeService.StaticBackgroundIsLight;
        DeckFooter.Background = highContrast ? SystemColors.WindowBrush : ColorBrush(lightBackground ? "#B0FFFFFF" : "#B0000000");
        DownloadOptionsPanel.Background = highContrast ? SystemColors.WindowBrush : ColorBrush(lightBackground ? "#F0FFFFFF" : "#F0101216");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad
    {
        public ushort Buttons; public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ControllerState { public uint Packet; public Gamepad Gamepad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out ControllerState state);
    private void PollController(object? sender, EventArgs e)
    {
        if (_controllerUnavailable || !IsVisible || _deckWindow?.IsActive != true) { _controllerButtons = 0; return; }
        try
        {
            ushort buttons = 0;
            for (uint index = 0; index < 4; index++) if (XInputGetState(index, out var state) == 0) { buttons = state.Gamepad.Buttons; break; }
            var pressed = (ushort)(buttons & ~_controllerButtons); _controllerButtons = buttons;
            if (_deckWindow is MainWindow { RootNavigationView.IsPaneOpen: true } menuWindow)
            {
                if ((pressed & 0x2000) != 0) menuWindow.RootNavigationView.IsPaneOpen = false;
                else if ((pressed & 0x1000) != 0) ActivateDeckFocus();
                else if ((pressed & 0xF) != 0) MoveDeckFocus(pressed);
                return;
            }
            if ((pressed & 0x2000) != 0) DownloadBack_Click(this, new RoutedEventArgs()); // B
            else if ((pressed & 0x10) != 0) DownloadOptions_Click(this, new RoutedEventArgs()); // Menu
            else if ((pressed & 0x20) != 0) DownloadMenu_Click(this, new RoutedEventArgs()); // View
            else if ((pressed & 0x1000) != 0)
            {
                if (Keyboard.FocusedElement is Button { IsEnabled: true }) ActivateDeckFocus();
                else DownloadToggle_Click(this, new RoutedEventArgs());
            }
            else if ((pressed & 0xF) != 0)
                MoveDeckFocus(pressed);
        }
        catch (DllNotFoundException) { _controllerUnavailable = true; _controllerTimer.Stop(); }
        catch (EntryPointNotFoundException) { _controllerUnavailable = true; _controllerTimer.Stop(); }
    }
    private void MoveDeckFocus(ushort buttons) => (Keyboard.FocusedElement as UIElement ?? this).MoveFocus(
        new TraversalRequest((buttons & 0x5) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
    private void ActivateDeckFocus()
    {
        if (Keyboard.FocusedElement is Button { IsEnabled: true } button)
        {
            if (button.Command is { } command) { if (command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter); }
            else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        else if (Keyboard.FocusedElement is Wpf.Ui.Controls.NavigationViewItem item && _deckWindow is MainWindow window)
        {
            if (item.TargetPageType is { } pageType) window.RootNavigationView.Navigate(pageType);
            else if (PresentationSource.FromVisual(window) is { } source)
                item.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }
}
