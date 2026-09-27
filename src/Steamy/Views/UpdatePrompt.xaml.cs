using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Steamy.Services;

namespace Steamy.Views;

/// <summary>Update card shown on top of the dimmed main window.</summary>
public partial class UpdatePrompt : UserControl
{
    private readonly IUpdateService _updates;
    private readonly UpdateInfo _update;
    private readonly TaskCompletionSource _closed = new();
    private CancellationTokenSource? _install;

    public UpdatePrompt(IUpdateService updates, UpdateInfo update)
    {
        InitializeComponent();
        _updates = updates;
        _update = update;
        CurrentVersionText.Text = updates.CurrentVersion.ToString();
        LatestVersionText.Text = update.Version.ToString();
    }

    /// <summary>Completes when the user dismisses the card.</summary>
    public Task Closed => _closed.Task;

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        _install = new CancellationTokenSource();
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressArea.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Updating…";
        LaterButton.Content = "Cancel";
        Bar.SmoothValue = 0;
        StatusText.Text = "Connecting…";

        try
        {
            await _updates.InstallAsync(_update, new Progress<UpdateProgress>(ShowProgress), _install.Token);
            Bar.SmoothValue = 100;
            TitleText.Text = "Restarting…";
            StatusText.Text = $"Steamy {_update.Version} is installed";
            LaterButton.IsEnabled = false;
            await Task.Delay(700);
            _updates.StartInstalledVersion();
            Application.Current.Shutdown();
        }
        catch (Exception exception) when (exception is OperationCanceledException or UpdateException or Win32Exception)
        {
            ProgressArea.Visibility = Visibility.Collapsed;
            ErrorText.Text = exception switch
            {
                OperationCanceledException => "Update cancelled — nothing was changed.",
                Win32Exception => "The update is installed. Please start Steamy again.",
                _ => exception.Message
            };
            ErrorText.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = exception is not Win32Exception;
            UpdateButton.Content = "Try again";
            LaterButton.IsEnabled = true;
            LaterButton.Content = "Later";
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
        StatusText.Text = progress.Stage == UpdateStage.Downloading && progress.TotalBytes > 0
            ? $"{DownloadFormat.Bytes(progress.Bytes)} of {DownloadFormat.Bytes(progress.TotalBytes)}  ·  {progress.Percent:0}%"
            : "Installing…";
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_install is not null)
        {
            _install.Cancel();
            return;
        }

        _closed.TrySetResult();
    }
}
