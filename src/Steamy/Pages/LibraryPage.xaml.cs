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
using System.Windows.Media.Animation;
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
    private ManifestSource _selectedSource = ManifestSource.Sushi;
    private string _downloadPath = string.Empty;
    private string _customArchivePath = string.Empty;
    private CancellationTokenSource? _availabilityCts;
    private CancellationTokenSource? _downloadCts;
    private bool _pauseRequested;
    private CancellationTokenSource? _artworkCts;
    private IInputElement? _previousFocus;
    private int _overlayRevision;
    private bool _overlayClosing;
    private bool _resumingExisting;
    private readonly System.Windows.Threading.DispatcherTimer _backdropResizeTimer = new(System.Windows.Threading.DispatcherPriority.Background)
    { Interval = TimeSpan.FromMilliseconds(140) };

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
        Unloaded += (_, _) => ResetOverlay();
        _backdropResizeTimer.Tick += (_, _) =>
        {
            _backdropResizeTimer.Stop();
            if (OverlayGrid.Visibility == Visibility.Visible) CaptureBackdrop();
        };
        DialogPanel.SizeChanged += (_, _) => DialogPanel.Clip = new RectangleGeometry(new Rect(DialogPanel.RenderSize), 24, 24);
        SizeChanged += (_, _) =>
        {
            DialogPanel.MaxWidth = Math.Max(0, ActualWidth - 32);
            DialogPanel.MaxHeight = Math.Max(0, ActualHeight - 32);
            if (OverlayGrid.Visibility == Visibility.Visible)
            {
                _backdropResizeTimer.Stop();
                _backdropResizeTimer.Start();
            }
        };
    }

    private void GameCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button border) return;
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
        OpenGameDetails(item);
    }

    public void OpenGameDetails(SteamCatalogItem item)
    {
        if (_overlayClosing || _downloadRunning) return;
        _selectedItem = item;
        _selectedSource = ManifestSource.Sushi;

        OverlayTitle.Text = item.Name;
        OverlayAppId.Text = $"App {item.AppId}";
        OverlayStatus.Text = "Choose a manifest source and download folder.";

        OverlayCover.Source = item.HeaderImage ?? item.ArtworkImage;
        _artworkCts?.Cancel();
        _artworkCts = new CancellationTokenSource();
        _ = LoadDetailArtworkAsync(item, _artworkCts);

        var requestedSource = ((LibraryViewModel)DataContext).SelectedSourceFilter;
        _selectedSource = Enum.TryParse<ManifestSource>(requestedSource, out var sourceFilter) ? sourceFilter : ManifestSource.Sushi;
        SetOptionState(SourceRyuu, _selectedSource == ManifestSource.Ryuu);
        SetOptionState(SourceZaza, _selectedSource == ManifestSource.Zaza);
        SetOptionState(SourceHubcap, _selectedSource == ManifestSource.Hubcap);
        SetOptionState(SourceDepotBox, _selectedSource == ManifestSource.DepotBox);
        SetOptionState(SourceSushi, _selectedSource == ManifestSource.Sushi);
        SourceAvailabilityText.Text = "";

        _ = CheckSourceAvailabilityAsync(_selectedSource, item.AppId);

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
        var resumeFolder = TargetFolderFor(item, settings, _downloadPath);
        var resumable = App.Services.GetRequiredService<IAppDataStore>().Downloads.FirstOrDefault(existing =>
            existing.AppId == item.AppId
            && DownloadJobPolicy.UsesModDownloader(existing.DownloadMode, existing.DepotId, existing.TargetFolder)
            && string.Equals(existing.TargetFolder, resumeFolder, StringComparison.OrdinalIgnoreCase)
            && existing.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled);
        _resumingExisting = resumable is not null;
        if (resumable is not null)
        {
            _availabilityCts?.Cancel();
            foreach (var name in Enum.GetNames<ManifestSource>())
                if (resumable.DownloadMode.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<ManifestSource>(name, out var originalSource))
                { _selectedSource = originalSource; break; }
            SetOptionState(SourceRyuu, _selectedSource == ManifestSource.Ryuu);
            SetOptionState(SourceZaza, _selectedSource == ManifestSource.Zaza);
            SetOptionState(SourceHubcap, _selectedSource == ManifestSource.Hubcap);
            SetOptionState(SourceDepotBox, _selectedSource == ManifestSource.DepotBox);
            SetOptionState(SourceSushi, _selectedSource == ManifestSource.Sushi);
            SourceAvailabilityText.Text = $"Resume uses the saved {_selectedSource} manifests and existing files.";
            SourceAvailabilityText.Foreground = TertiaryText;
        }
        StartButton.Content = resumable is null ? "Start download" : "Resume download";
        StartButton.IsEnabled = true;
        PauseButton.Visibility = Visibility.Collapsed;
        ShowOverlay();
    }

    private async Task LoadDetailArtworkAsync(SteamCatalogItem item, CancellationTokenSource cancellation)
    {
        try
        {
            var image = await App.Services.GetRequiredService<IArtworkService>().LoadHeroAsync(item.AppId, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_selectedItem, item) && image is not null)
                OverlayCover.Source = image;
        }
        catch (OperationCanceledException) { }
        catch { /* The already loaded cover remains available offline. */ }
        finally
        {
            if (ReferenceEquals(_artworkCts, cancellation)) _artworkCts = null;
            cancellation.Dispose();
        }
    }

    private void CaptureBackdrop()
    {
        // A frozen half-resolution snapshot avoids re-blurring the gallery for every live update.
        MainContentGrid.Opacity = 1;
        MainContentGrid.IsEnabled = true;
        if (MainContentGrid.ActualWidth > 0 && MainContentGrid.ActualHeight > 0 && !SystemParameters.HighContrast)
        {
            var snapshot = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(MainContentGrid.ActualWidth / 2)),
                Math.Max(1, (int)Math.Ceiling(MainContentGrid.ActualHeight / 2)), 48, 48, PixelFormats.Pbgra32);
            snapshot.Render(MainContentGrid);
            snapshot.Freeze();
            BackdropImage.Source = snapshot;
            BackdropImage.Visibility = Visibility.Visible;
            MainContentGrid.Opacity = 0;
        }
        MainContentGrid.IsEnabled = OverlayGrid.Visibility != Visibility.Visible;
    }

    private void ShowOverlay()
    {
        _overlayRevision++;
        _previousFocus = Keyboard.FocusedElement;
        CaptureBackdrop();
        MainContentGrid.IsHitTestVisible = false;
        MainContentGrid.IsEnabled = false;
        OverlayGrid.Visibility = Visibility.Visible;
        Animate(OverlayGrid, OpacityProperty, 0, 1);
        Animate(DialogScale, ScaleTransform.ScaleXProperty, 0.97, 1);
        Animate(DialogScale, ScaleTransform.ScaleYProperty, 0.97, 1);
        Animate(DialogOffset, TranslateTransform.YProperty, 10, 0);
        DialogCloseButton.Focus();
    }

    private void Animate(DependencyObject target, DependencyProperty property, double from, double to)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var revision = _overlayRevision;
        animation.Completed += (_, _) =>
        {
            if (revision != _overlayRevision) return;
            if (target is UIElement ui) ui.BeginAnimation(property, null);
            else if (target is Animatable transform) transform.BeginAnimation(property, null);
        };
        if (target is UIElement element) element.BeginAnimation(property, animation);
        else if (target is Animatable transform) transform.BeginAnimation(property, animation);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || OverlayGrid.Visibility != Visibility.Visible) return;
        e.Handled = true;
        CloseOverlay();
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
        if (_resumingExisting || _downloadRunning)
        {
            OverlayStatus.Text = "Continue with the original source and saved manifests. Manage this job in Downloads.";
            return;
        }
        if (sender is not Border border || !border.IsEnabled || border.Tag is not string sourceTag) return;
        if (!Enum.TryParse<ManifestSource>(sourceTag, out var source)) return;
        _selectedSource = source;
        SetOptionState(SourceRyuu, source == ManifestSource.Ryuu);
        SetOptionState(SourceZaza, source == ManifestSource.Zaza);
        SetOptionState(SourceHubcap, source == ManifestSource.Hubcap);
        SetOptionState(SourceDepotBox, source == ManifestSource.DepotBox);
        SetOptionState(SourceSushi, source == ManifestSource.Sushi);
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
        var cancellation = _availabilityCts;
        var ct = cancellation.Token;

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
        finally
        {
            if (ReferenceEquals(_availabilityCts, cancellation)) _availabilityCts = null;
            cancellation.Dispose();
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

    public async void CloseOverlay()
    {
        if (_overlayClosing || OverlayGrid.Visibility != Visibility.Visible) return;
        _overlayClosing = true;
        var revision = ++_overlayRevision;
        _availabilityCts?.Cancel();
        _artworkCts?.Cancel();
        if (SystemParameters.ClientAreaAnimation)
        {
            Animate(OverlayGrid, OpacityProperty, 1, 0);
            Animate(DialogScale, ScaleTransform.ScaleXProperty, 1, 0.98);
            Animate(DialogScale, ScaleTransform.ScaleYProperty, 1, 0.98);
            await Task.Delay(180);
        }
        if (revision != _overlayRevision) return;
        var focus = _previousFocus;
        ResetOverlay();
        if (focus is UIElement { IsVisible: true, IsEnabled: true }) Keyboard.Focus(focus);
    }

    private void ResetOverlay()
    {
        _backdropResizeTimer.Stop();
        _overlayRevision++;
        _availabilityCts?.Cancel();
        _artworkCts?.Cancel();
        OverlayGrid.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DialogOffset.BeginAnimation(TranslateTransform.YProperty, null);
        OverlayGrid.Visibility = Visibility.Collapsed;
        BackdropImage.Visibility = Visibility.Collapsed;
        BackdropImage.Source = null;
        OverlayCover.Source = null;
        MainContentGrid.Opacity = 1;
        MainContentGrid.IsHitTestVisible = true;
        MainContentGrid.IsEnabled = true;
        _previousFocus = null;
        _selectedItem = null;
        _resumingExisting = false;
        _overlayClosing = false;
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

        var targetFolder = TargetFolderFor(item, settings, _downloadPath);

        var store = App.Services.GetRequiredService<IAppDataStore>();
        var queueStore = App.Services.GetRequiredService<IDownloadQueueStore>();
        if (store.Downloads.Any(existing => existing.AppId == item.AppId
            && string.Equals(existing.TargetFolder, targetFolder, StringComparison.OrdinalIgnoreCase)
            && existing.State is DownloadJobState.Queued or DownloadJobState.Paused
            && !DownloadJobPolicy.UsesModDownloader(existing.DownloadMode, existing.DepotId, existing.TargetFolder)))
        {
            OverlayStatus.Text = "This folder already has a DepotDownloader job. Open Downloads to continue that job, or choose a different folder.";
            return;
        }
        if (store.Downloads.Any(existing => existing.AppId == item.AppId && existing.IsActive
            && string.Equals(existing.TargetFolder, targetFolder, StringComparison.OrdinalIgnoreCase)))
        {
            OverlayStatus.Text = "This game already has a download running. Open Downloads to manage it.";
            return;
        }

        // Reuse the job of a paused download of this app so Resume keeps its identity, target
        // folder and progress instead of piling up duplicate rows for the same game.
        var job = store.Downloads.FirstOrDefault(existing => existing.AppId == item.AppId
                && DownloadJobPolicy.UsesModDownloader(existing.DownloadMode, existing.DepotId, existing.TargetFolder)
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
        var downloadManager = App.Services.GetRequiredService<IDownloadManager>();
        using var cts = new CancellationTokenSource();
        try { downloadManager.RegisterJob(job.Id, cts); }
        catch (InvalidOperationException)
        {
            OverlayStatus.Text = "This download is still finishing its previous operation. Please try again shortly.";
            return;
        }
        try
        {
        var isResume = job.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled;
        if (isResume)
        {
            // A resume belongs to its original source and manifest snapshot.
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
        _pauseRequested = false;
        _downloadCts = cts;

        var source = _selectedSource;
        if (isResume)
        {
            foreach (var name in new[] { "Ryuu", "Zaza", "Hubcap", "DepotBox", "Sushi" })
                if (job.DownloadMode.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<ManifestSource>(name, out var originalSource))
                { source = originalSource; break; }
        }
        var token = cts.Token;
        using var progress = new Steamy.Controls.BufferedDownloadProgress(msg =>
        {
            if (token.IsCancellationRequested || !job.IsActive) return;
            if (!GameDownloadProgressMessage.TryApply(job, msg))
                job.Status = msg;

            if (_selectedItem == item)
                OverlayStatus.Text = job.IsActive ? $"{job.Status} — {job.ProgressLabel}" : job.Status;
        });

        try
        {
            var archivePath = _customArchivePath;
            // Resume continues from the cached manifests — no refresh first, otherwise the source
            // hands out newer manifests and DepotDownloader re-downloads everything.
            var result = isResume
                ? await Task.Run(() => ryuuService.ResumeDownloadAsync(item.AppId, targetFolder, progress, cts.Token))
                : await Task.Run(() => ryuuService.DownloadGameAsync(item.AppId, targetFolder, source, progress, cts.Token));
            token.ThrowIfCancellationRequested();
            if (isResume && !result.Succeeded && result.Message.Contains("No cached manifests", StringComparison.OrdinalIgnoreCase))
            {
                // Nothing cached (e.g. the app was reinstalled): fetch manifests once, then resume.
                await Dispatcher.BeginInvoke(() => job.Status = "No cached manifests — fetching from " + source + "…");
                await Task.Run(() => App.Services.GetRequiredService<IManifestSourceService>()
                    .DownloadManifestsAsync(source, item.AppId, progress, cts.Token));
                result = await Task.Run(() => ryuuService.ResumeDownloadAsync(item.AppId, targetFolder, progress, cts.Token));
            }

            token.ThrowIfCancellationRequested();
            if (result.Succeeded && !string.IsNullOrEmpty(archivePath) && File.Exists(archivePath))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    token.ThrowIfCancellationRequested();
                    job.Status = "Extracting custom archive...";
                    if (_selectedItem == item) OverlayStatus.Text = job.Status;
                });
                await Task.Run(() => { token.ThrowIfCancellationRequested(); ExtractArchive(archivePath, targetFolder); }, token);
            }

            await Dispatcher.BeginInvoke(() =>
            {
                token.ThrowIfCancellationRequested();
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
        }
        finally
        {
                try { await queueStore.SaveAsync(job); }
                finally
                {
                    downloadManager.UnregisterJob(job.Id);
                    _downloadCts = null;
                    if (_selectedItem == item) PauseButton.Visibility = Visibility.Collapsed;
                }
            }
    }

    private static string TargetFolderFor(SteamCatalogItem item, AppSettings settings, string selectedFolder)
    {
        var folder = string.IsNullOrWhiteSpace(selectedFolder) ? settings.DownloadFolder : selectedFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames");
        return Path.Combine(folder, SanitizeFolderName(item.Name));
    }

    private static string SanitizeFolderName(string name)
        => Regex.Replace(name, @"[<>:""/\\|?*]", "_").Trim();
}
