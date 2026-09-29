using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class LibraryPage : Page
{
    private SteamCatalogItem? _selectedItem;
    private ManifestSource _selectedSource = ManifestSource.Ryuu;
    private string _downloadPath = string.Empty;
    private string _customArchivePath = string.Empty;
    private CancellationTokenSource? _availabilityCts;
    private CancellationTokenSource? _downloadCts;
    private bool _pauseRequested;

    private Brush ActiveChipBg => ThemeBrush("AccentSoftBrush");
    private Brush ActiveChipFg => ThemeBrush("AccentBrush");
    private Brush InactiveChipBg => ThemeBrush("SurfaceBrush");
    private Brush InactiveChipFg => ThemeBrush("TextSecondaryBrush");
    private Brush InactiveBorder => ThemeBrush("BorderSubtleBrush");
    private Brush PrimaryText => ThemeBrush("TextPrimaryBrush");
    private Brush TertiaryText => ThemeBrush("TextTertiaryBrush");

    private Brush ThemeBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>
    /// WPF-UI 4.2 ships an EnumToBoolConverter whose Convert back-ends into an ArgumentException when
    /// the binding first activates. The page owns its own converter so the NSFW scope checkbox can bind
    /// directly without surprising the application at startup.
    /// </summary>
    public LibraryPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LibraryViewModel>();
        _ = ((LibraryViewModel)DataContext).OnNavigatedToAsync();
    }

    private void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        var item = border.Tag switch
        {
            SteamCatalogItem catalogItem => catalogItem,
            Game game => new SteamCatalogItem
            {
                AppId = game.AppId,
                Name = game.Name,
                IsInstalled = true,
                CapsuleImageUrl = game.ArtworkUrl,
                PortraitImageUrl = game.ArtworkUrl,
                ArtworkImage = game.ArtworkImage,
                HeaderImage = game.HeaderImage
            },
            _ => null
        };
        if (item is null) return;

        _selectedItem = item;
        _selectedSource = ManifestSource.Ryuu;

        OverlayTitle.Text = item.Name;
        OverlayAppId.Text = $"App {item.AppId}";
        OverlayStatus.Text = "Choose a manifest source and download folder.";

        try
        {
            var heroUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/library_hero.jpg";
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(heroUrl);
            bmp.DecodePixelHeight = 280;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            OverlayCover.Source = bmp;
        }
        catch { OverlayCover.Source = null; }

        SetOptionState(SourceRyuu, true);
        SetOptionState(SourceZaza, false);
        SetOptionState(SourceHubcap, false);
        SetOptionState(SourceDepotBox, false);
        SourceAvailabilityText.Text = "";

        _ = CheckSourceAvailabilityAsync(ManifestSource.Ryuu, item.AppId);

        var settings = App.Services.GetRequiredService<ISettingsService>().Load();
        _downloadPath = settings.DownloadFolder ?? string.Empty;
        DownloadPathText.Text = string.IsNullOrWhiteSpace(_downloadPath)
            ? "Select a folder…"
            : _downloadPath;
        DownloadPathText.Foreground = string.IsNullOrWhiteSpace(_downloadPath) ? TertiaryText : PrimaryText;

        _customArchivePath = string.Empty;
        CustomArchiveText.Text = "No archive selected";
        CustomArchiveText.Foreground = TertiaryText;
        // A paused download of this app resumes in the same folder with one click.
        var resumeFolder = Path.Combine(
            string.IsNullOrWhiteSpace(_downloadPath) ? (settings.DownloadFolder ?? string.Empty) : _downloadPath,
            SanitizeFolderName(item.Name));
        var resumable = App.Services.GetRequiredService<IAppDataStore>().Downloads.FirstOrDefault(existing =>
            existing.AppId == item.AppId
            && string.Equals(existing.TargetFolder, resumeFolder, StringComparison.OrdinalIgnoreCase)
            && existing.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled);
        StartButton.Content = resumable is null ? "Start download" : "Resume download";
        StartButton.IsEnabled = true;
        PauseButton.Visibility = Visibility.Collapsed;
        OverlayGrid.Visibility = Visibility.Visible;
    }

    private void SetOptionState(Border option, bool selected)
    {
        if (!option.IsEnabled) return;
        option.Background = selected ? ActiveChipBg : InactiveChipBg;
        option.BorderBrush = selected ? ActiveChipFg : InactiveBorder;
        if (option.Child is TextBlock text)
            text.Foreground = selected ? ActiveChipFg : InactiveChipFg;
    }

    private void Source_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || !border.IsEnabled || border.Tag is not string sourceTag) return;
        if (!Enum.TryParse<ManifestSource>(sourceTag, out var source)) return;
        _selectedSource = source;
        SetOptionState(SourceRyuu, source == ManifestSource.Ryuu);
        SetOptionState(SourceZaza, source == ManifestSource.Zaza);
        SetOptionState(SourceHubcap, source == ManifestSource.Hubcap);
        SetOptionState(SourceDepotBox, source == ManifestSource.DepotBox);
        var info = App.Services.GetRequiredService<IManifestSourceService>().Sources
            .FirstOrDefault(s => s.Source == source);
        OverlayStatus.Text = info is not null ? info.Description : $"{source} selected.";

        if (_selectedItem is not null)
            _ = CheckSourceAvailabilityAsync(source, _selectedItem.AppId);
    }

    private async Task CheckSourceAvailabilityAsync(ManifestSource source, int appId)
    {
        _availabilityCts?.Cancel();
        _availabilityCts = new CancellationTokenSource();
        var ct = _availabilityCts.Token;

        SourceAvailabilityText.Text = $"Checking {source}...";
        SourceAvailabilityText.Foreground = TertiaryText;
        // A check that is still running, or that could not reach the API at all, must never lock
        // the button: the user asked for this download and the source may simply be slow.
        StartButton.IsEnabled = true;

        try
        {
            var service = App.Services.GetRequiredService<IManifestSourceService>();
            var availability = await Task.Run(() => service.CheckAvailabilityAsync(source, appId, ct), ct);

            if (ct.IsCancellationRequested) return;

            SourceAvailabilityText.Text = availability.Message;

            if (availability.Available && availability.Certain)
            {
                SourceAvailabilityText.Foreground = ThemeBrush("SuccessBrush");
                StartButton.IsEnabled = true;
            }
            else if (!availability.Certain)
            {
                SourceAvailabilityText.Foreground = ThemeBrush("WarningBrush");
                StartButton.IsEnabled = true;
            }
            else
            {
                SourceAvailabilityText.Foreground = ThemeBrush("DangerBrush");
                StartButton.IsEnabled = false;
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                SourceAvailabilityText.Text = "Could not check availability";
                SourceAvailabilityText.Foreground = ThemeBrush("WarningBrush");
                StartButton.IsEnabled = true;
            }
        }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select download folder", Multiselect = false, ValidateNames = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _downloadPath = dialog.FolderName;
            DownloadPathText.Text = _downloadPath;
            DownloadPathText.Foreground = PrimaryText;
        }
    }

    private void BrowseArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an archive to include",
            Filter = "Archives|*.zip;*.7z;*.rar|ZIP|*.zip|7-Zip|*.7z|RAR|*.rar|All Files|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _customArchivePath = dialog.FileName;
            CustomArchiveText.Text = Path.GetFileName(dialog.FileName);
            CustomArchiveText.Foreground = PrimaryText;
        }
    }

    private static void ExtractArchive(string archivePath, string destDir)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, true);
            return;
        }
        using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            entry.WriteToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }

    private void Overlay_Close(object sender, MouseButtonEventArgs e) => CloseOverlay();
    private void OverlayCloseButton_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void CloseOverlay()
    {
        OverlayGrid.Visibility = Visibility.Collapsed;
        _selectedItem = null;
    }

    private bool _downloadRunning;

    private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartDownloadAsync();
        }
        catch (Exception exception)
        {
            OverlayStatus.Text = $"Error: {exception.Message}";
        }
    }

    /// <summary>Pause asks the running download to stop; Resume later continues in the same folder.</summary>
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is null) return;
        _pauseRequested = true;
        try { _downloadCts.Cancel(); } catch (ObjectDisposedException) { }
        PauseButton.IsEnabled = false;
        OverlayStatus.Text = "Pausing…";
    }

    private async Task StartDownloadAsync()
    {
        if (_selectedItem is null) return;
        // A second click on Start/Resume while the download runs must not spawn a second
        // DepotDownloaderMod over the same files.
        if (_downloadRunning) return;
        _downloadRunning = true;
        try
        {
            await RunDownloadAsync();
        }
        finally
        {
            _downloadRunning = false;
        }
    }

    private async Task RunDownloadAsync()
    {
        if (_selectedItem is null) return;

        var item = _selectedItem;
        var settings = App.Services.GetRequiredService<ISettingsService>().Load();

        var basePath = string.IsNullOrWhiteSpace(_downloadPath) ? settings.DownloadFolder : _downloadPath;
        var folder = string.IsNullOrWhiteSpace(basePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames")
            : basePath;
        var targetFolder = Path.Combine(folder, SanitizeFolderName(item.Name));

        var store = App.Services.GetRequiredService<IAppDataStore>();
        var queueStore = App.Services.GetRequiredService<IDownloadQueueStore>();

        // Reuse the job of a paused download of this app so Resume keeps its identity, target
        // folder and progress instead of piling up duplicate rows for the same game.
        var job = store.Downloads.FirstOrDefault(existing => existing.AppId == item.AppId
                && string.Equals(existing.TargetFolder, targetFolder, StringComparison.OrdinalIgnoreCase)
                && existing.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled)
            ?? new DownloadJob
        {
            AppId = item.AppId,
            GameName = item.Name,
            CoverImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/header.jpg",
            TargetFolder = targetFolder,
            Started = DateTime.Now,
            DownloadMode = $"DepotDownloaderMod ({_selectedSource})",
            AuthorizationConfirmed = true
        };
        var isResume = job.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled;
        if (isResume)
        {
            // Continue with the source the user just picked; the mode reflects it.
            job.DownloadMode = $"DepotDownloaderMod ({_selectedSource})";
            job.Finished = null;
        }
        job.State = DownloadJobState.Preparing;
        job.Status = isResume ? $"Resuming download from {_selectedSource}..." : $"Starting download from {_selectedSource}...";

        if (!store.Downloads.Contains(job)) store.Downloads.Insert(0, job);
        await queueStore.SaveAsync(job);

        StartButton.IsEnabled = false;
        StartButton.Content = "Downloading...";
        PauseButton.Visibility = Visibility.Visible;
        PauseButton.IsEnabled = true;
        OverlayStatus.Text = job.Status;

        var ryuuService = App.Services.GetRequiredService<IRyuuGameDownloadService>();
        var downloadManager = App.Services.GetRequiredService<IDownloadManager>();
        _pauseRequested = false;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        downloadManager.RegisterJob(job.Id, cts);

        var source = _selectedSource;
        var progress = new Progress<string>(msg => Dispatcher.BeginInvoke(() =>
        {
            if (!GameDownloadProgressMessage.TryApply(job, msg))
                job.Status = msg;

            if (_selectedItem == item)
                OverlayStatus.Text = job.IsActive ? $"{job.Status} — {job.ProgressLabel}" : job.Status;
        }));

        try
        {
            var archivePath = _customArchivePath;
            var result = isResume
                ? await Task.Run(() => ryuuService.ResumeDownloadAsync(item.AppId, targetFolder, progress, cts.Token))
                : await Task.Run(() => ryuuService.DownloadGameAsync(item.AppId, targetFolder, source, progress, cts.Token));

            if (result.Succeeded && !string.IsNullOrEmpty(archivePath) && File.Exists(archivePath))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    job.Status = "Extracting custom archive...";
                    if (_selectedItem == item) OverlayStatus.Text = job.Status;
                });
                await Task.Run(() => ExtractArchive(archivePath, targetFolder));
            }

            await Dispatcher.BeginInvoke(() =>
            {
                job.ClearLiveStats();
                if (result.Succeeded)
                {
                    job.State = DownloadJobState.Completed;
                    job.Progress = 100;
                    job.Status = result.Message;
                    job.Finished = DateTime.Now;
                }
                else
                {
                    job.State = DownloadJobState.Failed;
                    job.Status = result.Message;
                    job.Finished = DateTime.Now;
                }

                if (_selectedItem == item)
                {
                    OverlayStatus.Text = result.Succeeded
                        ? $"Done! {result.Message}"
                        : $"Failed: {result.Message}";
                    StartButton.Content = result.Succeeded ? "Completed" : "Retry";
                    StartButton.IsEnabled = !result.Succeeded;
                }
            });
        }
        catch (OperationCanceledException)
            {
                // The app-level pause flag wins over a plain cancel: both arrive as an
                // OperationCanceledException here, so the flag tells the two apart.
                var wasPaused = _pauseRequested || downloadManager.IsPauseRequested(job.Id);
                job.State = wasPaused ? DownloadJobState.Paused : DownloadJobState.Cancelled;
                job.Status = wasPaused ? "Paused — resume will continue from existing files" : "Download cancelled.";
                job.ClearLiveStats();
                if (_selectedItem == item)
                {
                    OverlayStatus.Text = job.Status;
                    StartButton.Content = wasPaused ? "Resume download" : "Start download";
                    StartButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                job.State = DownloadJobState.Failed;
                job.Status = ex.Message;
                job.Finished = DateTime.Now;
                if (_selectedItem == item)
                {
                    OverlayStatus.Text = $"Error: {ex.Message}";
                    StartButton.Content = "Retry";
                    StartButton.IsEnabled = true;
                }
            }
            finally
            {
                downloadManager.UnregisterJob(job.Id);
                _downloadCts = null;
                if (_selectedItem == item) PauseButton.Visibility = Visibility.Collapsed;
                await queueStore.SaveAsync(job);
            }
    }

    private static string SanitizeFolderName(string name)
        => Regex.Replace(name, @"[<>:""/\\|?*]", "_").Trim();
}
