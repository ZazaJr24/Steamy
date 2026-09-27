using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Steamy.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Steamy.Views;

public enum UpdateChoice { Later, Skip, Installing }

public partial class UpdateWindow : Window
{
    private readonly IUpdateService _updates;
    private readonly UpdateInfo _update;
    private readonly bool _whatsNew;
    private CancellationTokenSource? _install;

    private UpdateWindow(IUpdateService updates, UpdateInfo update, bool whatsNew, int activeDownloads)
    {
        InitializeComponent();
        _updates = updates;
        _update = update;
        _whatsNew = whatsNew;

        if (whatsNew)
        {
            HeaderIcon.Symbol = SymbolRegular.Sparkle24;
            TitleText.Text = $"Steamy was updated to {update.Version}";
            SubtitleText.Text = "Everything is in place — here is what changed.";
            PrimaryButton.Content = "Let's go";
            PrimaryButton.Icon = null;
            LaterButton.Visibility = Visibility.Collapsed;
            SkipButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleText.Text = "A new version of Steamy is ready";
            var details = new List<string> { $"Version {update.Version}", $"you have {updates.CurrentVersion}" };
            if (update.AssetSize > 0) details.Add(DownloadFormat.Bytes(update.AssetSize));
            if (update.PublishedAt is { } published) details.Add(published.LocalDateTime.ToString("dd.MM.yyyy"));
            SubtitleText.Text = string.Join("  ·  ", details);

            if (activeDownloads > 0)
            {
                DownloadsWarning.Text = $"{activeDownloads} download(s) are running. They are paused for the restart and can be resumed afterwards.";
                DownloadsWarning.Visibility = Visibility.Visible;
            }
        }

        ReleaseNotesRenderer.Render(update.Notes, NotesPanel);
    }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public static UpdateChoice ShowAvailable(Window? owner, IUpdateService updates, UpdateInfo update, int activeDownloads)
    {
        var window = new UpdateWindow(updates, update, whatsNew: false, activeDownloads);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Choice;
    }

    public static void ShowWhatsNew(Window? owner, IUpdateService updates, UpdateInfo update)
    {
        var window = new UpdateWindow(updates, update, whatsNew: true, activeDownloads: 0);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_whatsNew)
        {
            Close();
            return;
        }

        Choice = UpdateChoice.Installing;
        _install = new CancellationTokenSource();
        SetBusy(true);

        var progress = new Progress<UpdateProgress>(ShowProgress);
        try
        {
            await _updates.InstallAsync(_update, progress, _install.Token);
            Bar.SmoothValue = 100;
            PercentText.Text = string.Empty;
            StatusText.Text = "Installed — restarting Steamy…";
            LaterButton.IsEnabled = false;
            await Task.Delay(700);
            _updates.StartInstalledVersion();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            Choice = UpdateChoice.Later;
            SetBusy(false);
            ShowError("Update cancelled — nothing was changed.");
        }
        catch (UpdateException exception)
        {
            Choice = UpdateChoice.Later;
            SetBusy(false);
            ShowError(exception.Message);
        }
        catch (Win32Exception)
        {
            ShowError("The update is installed, but Steamy could not restart itself. Please start it again.");
            LaterButton.IsEnabled = true;
            LaterButton.Content = "Close";
        }
        finally
        {
            _install?.Dispose();
            _install = null;
        }
    }

    private void ShowProgress(UpdateProgress progress)
    {
        Bar.SmoothValue = progress.Percent;
        PercentText.Text = $"{progress.Percent:0.0}%";
        StatusText.Text = progress.Stage == UpdateStage.Downloading
            ? progress.TotalBytes > 0
                ? $"Downloading  {DownloadFormat.Bytes(progress.Bytes)} of {DownloadFormat.Bytes(progress.TotalBytes)}  ·  {DownloadFormat.Speed(progress.BytesPerSecond)}"
                : $"Downloading  {DownloadFormat.Bytes(progress.Bytes)}"
            : "Installing…";
    }

    private void SetBusy(bool busy)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressArea.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy;
        PrimaryButton.Content = busy ? "Updating…" : "Try again";
        SkipButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        LaterButton.Content = busy ? "Cancel" : "Later";
        if (busy)
        {
            Bar.SmoothValue = 0;
            PercentText.Text = "0.0%";
            StatusText.Text = "Connecting…";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_install is not null)
        {
            _install.Cancel();
            return;
        }

        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _updates.SkipVersion(_update.Version);
        Choice = UpdateChoice.Skip;
        Close();
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_update.PageUrl.ToString()) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _install?.Cancel();
        base.OnClosing(e);
    }
}

/// <summary>Turns the Markdown of a GitHub release into simple, readable text blocks.</summary>
public static partial class ReleaseNotesRenderer
{
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\s+by @\S+ in https?://\S+$")]
    private static partial Regex AttributionPattern();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex EmphasisPattern();

    public static void Render(string markdown, Panel target)
    {
        target.Children.Clear();
        var lines = (markdown ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal)) continue;

            if (line.StartsWith('#'))
            {
                target.Children.Add(Text(Clean(line.TrimStart('#')), 13.5, FontWeights.SemiBold, "TextPrimaryBrush",
                    new Thickness(0, target.Children.Count == 0 ? 0 : 12, 0, 4)));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bullet = Text("•", 12.5, FontWeights.Bold, "AccentBrush", new Thickness(2, 0, 10, 0));
                var text = Text(Clean(line[2..]), 12.5, FontWeights.Normal, "TextSecondaryBrush", new Thickness(0));
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet);
                row.Children.Add(text);
                target.Children.Add(row);
            }
            else
            {
                target.Children.Add(Text(Clean(line), 12.5, FontWeights.Normal, "TextSecondaryBrush", new Thickness(0, 2, 0, 2)));
            }
        }

        if (target.Children.Count == 0)
            target.Children.Add(Text("No release notes were published for this version.", 12.5, FontWeights.Normal, "TextTertiaryBrush", new Thickness(0)));
    }

    private static string Clean(string text)
    {
        text = LinkPattern().Replace(text, "$1");
        text = AttributionPattern().Replace(text, string.Empty);
        return EmphasisPattern().Replace(text, string.Empty).Trim();
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Margin = margin, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
