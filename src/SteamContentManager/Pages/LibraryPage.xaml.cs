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
using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class LibraryPage : Page
{
    private SteamCatalogItem? _selectedItem;
    private ManifestSource _selectedSource = ManifestSource.Ryuu;
    private string _downloadPath = string.Empty;
    private string _customArchivePath = string.Empty;
    private CancellationTokenSource? _availabilityCts;

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
        SetOptionState(SourceResonance, false);
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
        StartButton.Content = "Start download";
        StartButton.IsEnabled = true;
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
        SetOptionState(SourceResonance, source == ManifestSource.Resonance);
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

        SourceAvailabilityText.Text = "Checking availability...";
        SourceAvailabilityText.Foreground = TertiaryText;
        StartButton.IsEnabled = false;

        try
        {
            var service = App.Services.GetRequiredService<IManifestSourceService>();
            var available = await Task.Run(() => service.IsAvailableAsync(source, appId, ct), ct);

            if (ct.IsCancellationRequested) return;

            if (available)
            {
                SourceAvailabilityText.Text = $"Available on {source}";
                SourceAvailabilityText.Foreground = ThemeBrush("SuccessBrush");
                StartButton.IsEnabled = true;
            }
            else
            {
                SourceAvailabilityText.Text = $"Not available on {source}";
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
        using var archive = ArchiveFactory.Open(stream);
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

    private async Task StartDownloadAsync()
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

        var job = new DownloadJob
        {
            AppId = item.AppId,
            GameName = item.Name,
            CoverImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/header.jpg",
            TargetFolder = targetFolder,
            Started = DateTime.Now,
            DownloadMode = $"DepotDownloaderMod ({_selectedSource})",
            AuthorizationConfirmed = true
        };
        job.State = DownloadJobState.Preparing;
        job.Status = $"Starting download from {_selectedSource}...";

        store.Downloads.Insert(0, job);
        await queueStore.SaveAsync(job);

        StartButton.IsEnabled = false;
        StartButton.Content = "Downloading...";
        OverlayStatus.Text = $"Download queued — check Downloads tab for progress.";

        var ryuuService = App.Services.GetRequiredService<IRyuuGameDownloadService>();
        var source = _selectedSource;
        var lastDiskCheck = DateTime.UtcNow;
        var lastDiskBytes = 0L;
        var progress = new Progress<string>(msg => Dispatcher.BeginInvoke(() =>
        {
            job.State = DownloadJobState.Downloading;

            if (msg.StartsWith("PROGRESS|", StringComparison.Ordinal))
            {
                var parts = msg.Split('|');
                if (parts.Length >= 10
                    && double.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    var depotIndex = parts[2];
                    var totalDepots = parts[3];
                    if (int.TryParse(depotIndex, out var dIdx) && int.TryParse(totalDepots, out var dTotal) && dTotal > 0)
                        job.Progress = ((dIdx - 1) * 100.0 + pct) / dTotal;
                    else
                        job.Progress = pct;
                    if (!string.IsNullOrWhiteSpace(parts[5])) job.Downloaded = parts[5];
                    if (!string.IsNullOrWhiteSpace(parts[6])) job.TotalSize = parts[6];
                    if (!string.IsNullOrWhiteSpace(parts[7])) job.Speed = parts[7];
                    if (!string.IsNullOrWhiteSpace(parts[8])) job.Eta = parts[8];
                    if (!string.IsNullOrWhiteSpace(parts[9])) job.CurrentFile = parts[9];
                    job.Status = $"Downloading depot {depotIndex}/{totalDepots} — {job.Progress:0.#}%";

                    var currentBytes = ParseBytesValue(parts[5]);
                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastDiskCheck).TotalSeconds;
                    if (elapsed >= 1.0 && currentBytes > lastDiskBytes)
                    {
                        var bytesPerSec = (currentBytes - lastDiskBytes) / elapsed;
                        job.DiskSpeed = FormatSpeed(bytesPerSec);
                        lastDiskBytes = currentBytes;
                        lastDiskCheck = now;
                    }

                    if (string.IsNullOrWhiteSpace(job.Eta) && pct > 0 && !string.IsNullOrWhiteSpace(parts[7]))
                    {
                        var totalBytes = ParseBytesValue(parts[6]);
                        var netSpeed = ParseBytesValue(parts[7]);
                        if (netSpeed > 0 && totalBytes > currentBytes)
                        {
                            var secsLeft = (totalBytes - currentBytes) / netSpeed;
                            job.Eta = secsLeft < 60 ? $"{secsLeft:F0}s"
                                    : secsLeft < 3600 ? $"{secsLeft / 60:F0}m {secsLeft % 60:F0}s"
                                    : $"{secsLeft / 3600:F0}h {(secsLeft % 3600) / 60:F0}m";
                        }
                    }
                }
            }
            else
            {
                job.Status = msg;
            }

            if (_selectedItem == item)
                OverlayStatus.Text = job.Status;
        }));

        try
        {
            var archivePath = _customArchivePath;
            var result = await Task.Run(() =>
                ryuuService.DownloadGameAsync(item.AppId, targetFolder, source, progress));

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
            job.State = DownloadJobState.Cancelled;
            job.Status = "Download cancelled.";
            if (_selectedItem == item)
            {
                OverlayStatus.Text = "Download cancelled.";
                StartButton.Content = "Start download";
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

        await queueStore.SaveAsync(job);
    }

    private static string SanitizeFolderName(string name)
        => Regex.Replace(name, @"[<>:""/\\|?*]", "_").Trim();

    private static readonly Regex BytesValuePattern = new(
        @"([\d.,]+)\s*(TiB|TB|GiB|GB|MiB|MB|KiB|KB|bytes?|B)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static long ParseBytesValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = BytesValuePattern.Match(text);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var val))
            return 0;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "TIB" or "TB" => (long)(val * 1024L * 1024 * 1024 * 1024),
            "GIB" or "GB" => (long)(val * 1024L * 1024 * 1024),
            "MIB" or "MB" => (long)(val * 1024L * 1024),
            "KIB" or "KB" => (long)(val * 1024),
            _ => (long)val
        };
    }

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 * 1024 => $"{bytesPerSec / (1024 * 1024 * 1024):F1} GB/s",
        >= 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):F1} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:F1} KB/s",
        _ => $"{bytesPerSec:F0} B/s"
    };
}
