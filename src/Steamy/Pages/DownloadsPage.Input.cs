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

    private void AttachDeckInput()
    {
        _deckWindow = Window.GetWindow(this);
        if (_deckWindow is not null) _deckWindow.PreviewKeyDown += DeckKeyDown;
    }
    private void DetachDeckInput()
    {
        if (_deckWindow is not null) _deckWindow.PreviewKeyDown -= DeckKeyDown;
        _deckWindow = null;
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
        if (_deckWindow is MainWindow { IsSocialDockOpen: true, SocialDockPage.IsKeyboardFocusWithin: true }) return;
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
        BrushResources.Set(Resources, "DeckBackground", highContrast ? SystemColors.WindowBrush : ColorBrush(UiThemeService.IsLight ? "#FFFFFF" : "#202124"));
        BrushResources.Set(Resources, "DeckHeroBackground", highContrast ? SystemColors.WindowBrush : (Brush)FindResource("GlassSurfaceBrush"));
        foreach (var (key, dark, light) in new[] { ("DeckText", "#F1F2F3", "#20212B"), ("DeckSecondary", "#B8BBC4", "#4F5362"), ("DeckMuted", "#9297A4", "#646A79") })
            BrushResources.Set(Resources, key, highContrast ? SystemColors.WindowTextBrush : ColorBrush(UiThemeService.IsLight ? light : dark));
        BrushResources.Set(Resources, "DeckBlue", highContrast ? SystemColors.HighlightBrush : ColorBrush(UiThemeService.IsLight ? "#2563EB" : "#79BDE8"));
        BrushResources.Set(Resources, "DeckGreen", highContrast ? SystemColors.HighlightBrush : ColorBrush(UiThemeService.IsLight ? "#387510" : "#A4D007"));
        BrushResources.Set(Resources, "DeckLine", highContrast ? SystemColors.GrayTextBrush : ColorBrush(UiThemeService.IsLight ? "#D9DCE5" : "#3A3C42"));
        DeckFooter.Background = highContrast ? SystemColors.WindowBrush : (Brush)FindResource("GlassSurfaceBrush");
        DownloadOptionsPanel.Background = highContrast ? SystemColors.WindowBrush : (Brush)FindResource("DialogSurfaceBrush");
    }
}
