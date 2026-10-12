using System.Windows;
using System.Windows.Controls;
using Steamy.Services;

namespace Steamy.Views;

public partial class TransparencyNotice : UserControl
{
    public static string CurrentAcknowledgement => BuildStamp.Version + ":transparency-2";
    private readonly ISettingsService _settings;
    private bool _read;
    public event EventHandler? Completed;
    public TransparencyNotice(ISettingsService settings)
    {
        _settings = settings; InitializeComponent();
        Loaded += (_, _) => { NoticeScroll.UpdateLayout(); UpdateReadState(); };
    }
    private void NoticeScroll_Changed(object sender, ScrollChangedEventArgs e) => UpdateReadState();
    private void UpdateReadState()
    {
        if (Acknowledge is null || NoticeScroll.ExtentHeight <= 0) return;
        _read |= NoticeScroll.VerticalOffset >= NoticeScroll.ScrollableHeight - 1;
        Acknowledge.IsEnabled = _read;
        ReadHint.Text = _read ? "Read through. Check the box to continue." : "Scroll to the end to enable confirmation.";
    }
    private void AcknowledgementChanged(object sender, RoutedEventArgs e)
    { if (ContinueButton is not null) ContinueButton.IsEnabled = _read && Acknowledge.IsChecked == true; }
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!_read || Acknowledge.IsChecked != true) return;
        ContinueButton.IsEnabled = false;
        try
        {
            var settings = _settings.Load(); var previous = settings.TransparencyAcknowledgement;
            settings.TransparencyAcknowledgement = CurrentAcknowledgement;
            settings.FirstRunCompleted ??= !App.ShouldShowFirstRunSetup;
            try { await _settings.SaveAsync(settings); }
            catch { settings.TransparencyAcknowledgement = previous; throw; }
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { ReadHint.Text = $"Confirmation could not be saved: {error.Message}"; ContinueButton.IsEnabled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
