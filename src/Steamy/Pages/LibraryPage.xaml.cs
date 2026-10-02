using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class LibraryPage : Page
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReservedTargets = new(StringComparer.OrdinalIgnoreCase);
    private SteamCatalogItem? _selectedItem;
    private ManifestSource _selectedSource = ManifestSource.Sushi;
    private string _downloadPath = string.Empty;
    private string _customArchivePath = string.Empty;
    private CancellationTokenSource? _availabilityCts;
    private CancellationTokenSource? _downloadCts;
    private Action? _pauseCurrentDownload;
    private int _downloadRevision;
    private bool _downloadCompleted;
    private CancellationTokenSource? _artworkCts;
    private IInputElement? _previousFocus;
    private int _overlayRevision;
    private bool _overlayClosing;
    private bool _resumingExisting;
    private DownloadJob? _resumeJob;
    private int _wizardStep;
    private bool _showingDownloadSetup;
    private bool _wizardBusy;
    private bool _sourceUnavailable;
    private string? _localPackagePath;
    private PreparedGameDownload? _preparedDownload;
    private CancellationTokenSource? _preparationCts;
    private readonly ObservableCollection<DownloadDepotChoice> _depotChoices = new();
    private int _locationRevision;
    private bool _batchDepotSelection;
    public int DownloadWizardStep => _showingDownloadSetup ? _wizardStep : -1;
    public IReadOnlyList<DownloadDepotChoice> DownloadDepotChoices => _depotChoices;
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
        DepotChoices.ItemsSource = _depotChoices;
        DataContext = App.Services.GetRequiredService<LibraryViewModel>();
        Loaded += (_, _) =>
        {
            var model = (LibraryViewModel)DataContext;
            if (model.RequestedDownload is not { } requested) return;
            model.RequestedDownload = null;
            OpenDownloadSetup(requested);
        };
        _ = ((LibraryViewModel)DataContext).OnNavigatedToAsync();
        Unloaded += (_, _) => ResetOverlay();
        _backdropResizeTimer.Tick += (_, _) =>
        {
            _backdropResizeTimer.Stop();
            if (OverlayGrid.Visibility == Visibility.Visible) CaptureBackdrop();
        };
        DialogPanel.SizeChanged += (_, _) => DialogPanel.Clip = new RectangleGeometry(new Rect(DialogPanel.RenderSize), 16, 16);
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

    public void OpenGameDetails(SteamCatalogItem item) => OpenGame(item, setup: true);
    public void OpenDownloadSetup(SteamCatalogItem item) => OpenGame(item, setup: true);

    private void OpenGame(SteamCatalogItem item, bool setup)
    {
        if (_overlayClosing || _downloadRunning) return;
        ResetPreparedDownload();
        _localPackagePath = null;
        LocalPackageName.Text = "No package selected";
        LocalPackagePanel.Visibility = Visibility.Collapsed;
        _showingDownloadSetup = setup;
        ConfigureOverlayLayout();
        SourceStepPanel.IsEnabled = true;
        _selectedItem = item;
        _selectedSource = ManifestSource.Sushi;

        OverlayTitle.Text = item.Name;
        OverlayAppId.Text = $"App {item.AppId}";
        _downloadCompleted = false;
        FavoriteButton.Content = "Favorite";
        FavoriteButton.IsEnabled = true;
        PlayButton.Visibility = App.Services.GetRequiredService<IAppDataStore>().Games.Any(game => game.AppId == item.AppId && game.InstallState == GameInstallState.Installed)
            ? Visibility.Visible : Visibility.Collapsed;
        _ = UpdateFavoriteButtonAsync(item);
        GameInstallInfo.Text = PlayButton.Visibility == Visibility.Visible ? "Installed in your Steam library." : "Not installed in your Steam library.";
        OverlayStatus.Text = "";

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
        SetOptionState(SourceLocal, _selectedSource == ManifestSource.Local);
        SourceAvailabilityText.Text = "";

        _availabilityCts?.Cancel();
        _sourceUnavailable = false;
        if (setup) _ = CheckSourceAvailabilityAsync(_selectedSource, item.AppId);

        var settings = App.Services.GetRequiredService<ISettingsService>().Load();
        _downloadPath = string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames") : settings.DownloadFolder;
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
        _resumeJob = resumable;
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
        SetOptionState(SourceLocal, _selectedSource == ManifestSource.Local);
            SourceAvailabilityText.Text = $"Resume uses the saved {_selectedSource} manifests and existing files.";
            SourceAvailabilityText.Foreground = TertiaryText;
        }
        SetWizardStep(resumable is null ? 0 : 2);
        if (resumable is not null && setup)
        {
            OverlayStatus.Text = $"Resume retains the original {_selectedSource} source, depot versions and game folder.";
            LocationSelectionText.Text = $"Saved {_selectedSource} download · depot versions are retained";
        }
        PauseButton.Visibility = Visibility.Collapsed;
        ShowOverlay();
    }

    private void ConfigureOverlayLayout()
    {
        DialogPanel.Width = 680;
        DialogPanel.HorizontalAlignment = HorizontalAlignment.Center;
        DialogPanel.VerticalAlignment = VerticalAlignment.Center;
        DialogPanel.Margin = new Thickness(16);
        DialogPanel.BorderThickness = new Thickness(1);
        DialogPanel.CornerRadius = new CornerRadius(16);
        DialogPanel.SetResourceReference(Border.BackgroundProperty, "GameDialogSurfaceBrush");
        DialogPanel.MaxWidth = Math.Max(0, ActualWidth - 32);
        DialogPanel.MaxHeight = Math.Max(0, ActualHeight - 32);
        DetailHeader.Visibility = Visibility.Visible;
        SetupHeader.Visibility = Visibility.Collapsed;
        DialogDimmer.Visibility = Visibility.Visible;
        GameInfoPanel.Visibility = Visibility.Visible;
        WizardHeader.Visibility = Visibility.Visible;
    }

    private void OpenDepotInfo_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: int depotId } || depotId <= 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://steamdb.info/depot/{depotId}/") { UseShellExecute = true }); }
        catch (Exception exception) { OverlayStatus.Text = $"Could not open depot information: {exception.Message}"; }
    }

    private void DepotVersion_PreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } combo) return;
        // Wheel input after picking a version should scroll the depot list, not silently
        // change the pinned manifest in the still-focused closed selector.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(combo); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            args.Handled = true;
            scroll.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
                { RoutedEvent = Mouse.MouseWheelEvent });
            break;
        }
    }

    private async Task UpdateFavoriteButtonAsync(SteamCatalogItem item)
    {
        try
        {
            var snapshot = await App.Services.GetRequiredService<IGameActivityService>().GetAsync();
            if (ReferenceEquals(_selectedItem, item))
                FavoriteButton.Content = snapshot.FavoriteAppIds.Contains(item.AppId) ? "Favorited" : "Favorite";
        }
        catch (Exception exception)
        { App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Warning, "Library", $"Favorites could not be read: {exception.Message}"); }
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedItem is not { } item) return;
        FavoriteButton.IsEnabled = false;
        try
        {
            var favorite = await App.Services.GetRequiredService<IGameActivityService>().ToggleFavoriteAsync(item.AppId);
            if (ReferenceEquals(_selectedItem, item)) FavoriteButton.Content = favorite ? "Favorited" : "Favorite";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_selectedItem, item)) OverlayStatus.Text = "Your favorite could not be saved. Check access to the Steamy data folder.";
            App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Warning, "Library", $"Favorite could not be saved: {exception.Message}");
        }
        finally { if (ReferenceEquals(_selectedItem, item)) FavoriteButton.IsEnabled = true; }
    }

    private async void AddToSteamTools_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedItem is not { } item || _wizardBusy || _downloadRunning) return;
        var tools = App.Services.GetRequiredService<BetterSteamToolsViewModel>();
        if (!tools.IsBusy)
        {
            tools.GameInput = item.AppId.ToString(CultureInfo.InvariantCulture);
            tools.SelectedSource = tools.Sources[0];
            tools.RefreshDetection();
        }
        ResetOverlay();
        App.Services.GetRequiredService<INavigationService>().Navigate<BetterSteamToolsPage>();
        if (tools.AddGameCommand.CanExecute(null)) await tools.AddGameCommand.ExecuteAsync(null);
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedItem is not { } item || !App.Services.GetRequiredService<IAppDataStore>().Games
            .Any(game => game.AppId == item.AppId && game.InstallState == GameInstallState.Installed)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"steam://rungameid/{item.AppId}") { UseShellExecute = true });
            await App.Services.GetRequiredService<IGameActivityService>().RecordLaunchAsync(item.AppId);
            if (ReferenceEquals(_selectedItem, item)) OverlayStatus.Text = "Launch requested in Steam.";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_selectedItem, item)) OverlayStatus.Text = $"Could not open Steam: {exception.Message}";
        }
    }

    private void NewSelectionButton_Click(object sender, RoutedEventArgs args)
    {
        if (_downloadRunning || _wizardBusy || _selectedItem is null) return;
        ResetPreparedDownload();
        _resumingExisting = false;
        _resumeJob = null;
        SelectDownloadSource(_selectedSource);
        OverlayStatus.Text = "Choose source and depot versions again. Existing files remain in place; choose another folder to keep the saved job separate.";
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
        MainContentGrid.Visibility = Visibility.Visible;
        MainContentGrid.Opacity = 1;
        MainContentGrid.IsEnabled = true;
        if (MainContentGrid.ActualWidth > 0 && MainContentGrid.ActualHeight > 0 && Steamy.Controls.MotionPreferences.BackdropBlurEnabled)
        {
            var snapshot = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(MainContentGrid.ActualWidth / 2)),
                Math.Max(1, (int)Math.Ceiling(MainContentGrid.ActualHeight / 2)), 48, 48, PixelFormats.Pbgra32);
            // Render in local coordinates. Rendering the positioned Grid directly also
            // includes its parent offset, which moved the blurred gallery right/down.
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(MainContentGrid) { Stretch = Stretch.Fill }, null,
                    new Rect(0, 0, MainContentGrid.ActualWidth, MainContentGrid.ActualHeight));
            snapshot.Render(drawing);
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
        if (OverlayGrid.Visibility != Visibility.Visible) _previousFocus = Keyboard.FocusedElement;
        OverlayGrid.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DialogOffset.BeginAnimation(TranslateTransform.YProperty, null);
        CaptureBackdrop();
        MainContentGrid.IsHitTestVisible = false;
        MainContentGrid.IsEnabled = false;
        MainContentGrid.Visibility = Visibility.Visible;
        OverlayGrid.Visibility = Visibility.Visible;
        Animate(OverlayGrid, OpacityProperty, 0, 1);
        DialogCloseButton.Focus();
    }

    private void Animate(DependencyObject target, DependencyProperty property, double from, double to)
    {
        if (!Steamy.Controls.MotionPreferences.AnimationsEnabled) return;
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

    private void Gallery_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        // Mouse opening and focus restoration preserve the user's gallery position.
        if (!Steamy.Controls.ScrollNavigation.IsNavigationKeyDown()) args.Handled = true;
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || OverlayGrid.Visibility != Visibility.Visible) return;
        e.Handled = true;
        CloseOverlay();
    }

    private void SetOptionState(Control option, bool selected)
    {
        option.Background = selected ? ActiveChipBg : InactiveChipBg;
        option.BorderBrush = selected ? ActiveChipFg : InactiveBorder;
        option.Foreground = selected ? ActiveChipFg : InactiveChipFg;
        System.Windows.Automation.AutomationProperties.SetItemStatus(option, selected ? "Selected" : "Not selected");
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ManifestSource>(tag, out var source))
            SelectDownloadSource(source);
    }

    public void SelectDownloadSource(ManifestSource source)
    {
        if (_resumingExisting || _downloadRunning)
        {
            OverlayStatus.Text = "Continue with the original source and saved manifests. Manage this job in Downloads.";
            return;
        }
        if (!Enum.IsDefined(source)) return;
        if (_selectedSource != source) ResetPreparedDownload();
        _selectedSource = source;
        _sourceUnavailable = false;
        SetWizardStep(0);
        SetOptionState(SourceRyuu, source == ManifestSource.Ryuu);
        SetOptionState(SourceZaza, source == ManifestSource.Zaza);
        SetOptionState(SourceHubcap, source == ManifestSource.Hubcap);
        SetOptionState(SourceDepotBox, source == ManifestSource.DepotBox);
        SetOptionState(SourceSushi, source == ManifestSource.Sushi);
        SetOptionState(SourceLocal, source == ManifestSource.Local);
        LocalPackagePanel.Visibility = source == ManifestSource.Local ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_selectedSource != source || _wizardStep != 0) return;
            if (source == ManifestSource.Local) SourceStepPanel.ScrollToEnd();
            else SourceStepPanel.ScrollToTop();
        }));
        OverlayStatus.Text = "";

        if (source == ManifestSource.Local)
        {
            _availabilityCts?.Cancel();
            SourceAvailabilityText.Text = "Select a local metadata package. It will be checked before depot selection.";
            SourceAvailabilityText.Foreground = TertiaryText;
            UpdateWizardControls();
        }
        else if (_selectedItem is not null)
            _ = CheckSourceAvailabilityAsync(source, _selectedItem.AppId);
    }

    private void BrowseLocalPackage_Click(object sender, RoutedEventArgs args)
    {
        if (_wizardBusy || _resumingExisting || _downloadRunning) return;
        var dialog = new OpenFileDialog { Title = "Select your metadata package", Filter = "Metadata packages (*.zip;*.lua)|*.zip;*.lua", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetLocalPackage(dialog.FileName);
    }

    private void SetLocalPackage(string path)
    {
        if (_wizardBusy || _resumingExisting || _downloadRunning) return;
        ResetPreparedDownload();
        _localPackagePath = path;
        LocalPackageName.Text = Path.GetFileName(path);
        OverlayStatus.Text = "Ready to verify this package.";
        UpdateWizardControls();
    }

    private void LocalPackage_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = !_wizardBusy && !_resumingExisting && !_downloadRunning && args.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private void LocalPackage_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (args.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) SetLocalPackage(files[0]);
        else OverlayStatus.Text = "Drop one ZIP or Lua package at a time.";
    }

    public void SelectLocalPackage(string path)
    {
        if (_wizardBusy || _resumingExisting || _downloadRunning) return;
        SelectDownloadSource(ManifestSource.Local);
        SetLocalPackage(path);
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
        _sourceUnavailable = false;
        UpdateWizardControls();

        try
        {
            var service = App.Services.GetRequiredService<IManifestSourceService>();
            var availability = await Task.Run(() => service.CheckAvailabilityAsync(source, appId, ct), ct);

            if (ct.IsCancellationRequested) return;

            SourceAvailabilityText.Text = availability.Message;

            if (availability.Available && availability.Certain)
            {
                SourceAvailabilityText.Foreground = ThemeBrush("SuccessBrush");
                _sourceUnavailable = false;
            }
            else if (!availability.Certain)
            {
                SourceAvailabilityText.Foreground = ThemeBrush("WarningBrush");
                _sourceUnavailable = false;
            }
            else
            {
                SourceAvailabilityText.Foreground = ThemeBrush("DangerBrush");
                _sourceUnavailable = true;
            }
            UpdateWizardControls();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                SourceAvailabilityText.Text = "Could not check availability";
                SourceAvailabilityText.Foreground = ThemeBrush("WarningBrush");
                _sourceUnavailable = false;
                UpdateWizardControls();
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
            ConfigureDownloadLocation(dialog.FolderName);
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

    private static void ExtractArchive(string archivePath, string destDir, CancellationToken cancellationToken)
    {
        var result = ArchiveExtractor.Extract(archivePath, destDir, cancellationToken: cancellationToken);
        if (!result.Succeeded) throw new InvalidDataException(result.Message);
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
        _preparationCts?.Cancel();
        if (!_showingDownloadSetup && Steamy.Controls.MotionPreferences.AnimationsEnabled)
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
        _locationRevision++;
        if (!_downloadRunning) ResetPreparedDownload();
        else
        {
            _preparationCts?.Cancel();
            // The running operation owns its snapshot until its final queue save.
            _preparedDownload = null;
            foreach (var choice in _depotChoices) choice.PropertyChanged -= DepotChoiceChanged;
            _depotChoices.Clear();
            _wizardBusy = false;
        }
        _downloadRevision++;
        _downloadRunning = false;
        _downloadCts = null;
        _pauseCurrentDownload = null;
        OverlayGrid.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DialogOffset.BeginAnimation(TranslateTransform.YProperty, null);
        OverlayGrid.Visibility = Visibility.Collapsed;
        BackdropImage.Visibility = Visibility.Collapsed;
        BackdropImage.Source = null;
        OverlayCover.Source = null;
        MainContentGrid.Visibility = Visibility.Visible;
        MainContentGrid.Opacity = 1;
        MainContentGrid.IsHitTestVisible = true;
        MainContentGrid.IsEnabled = true;
        _previousFocus = null;
        _selectedItem = null;
        _resumingExisting = false;
        _resumeJob = null;
        _overlayClosing = false;
    }

    private bool _downloadRunning;

    private void SetWizardStep(int step)
    {
        _wizardStep = Math.Clamp(step, 0, 2);
        SourceStepPanel.Visibility = _showingDownloadSetup && step == 0 ? Visibility.Visible : Visibility.Collapsed;
        DepotStepPanel.Visibility = _showingDownloadSetup && step == 1 ? Visibility.Visible : Visibility.Collapsed;
        LocationStepPanel.Visibility = _showingDownloadSetup && step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WizardStepText.Text = step switch { 0 => "Source", 1 => "Depots", _ => _resumingExisting ? "Resume download" : "Location" };
        WizardTrailText.Text = _resumingExisting ? "Original source · saved depot versions · existing files" : "Source  →  Depots & version  →  Location";
        if (step == 2) ConfigureDownloadLocation(_downloadPath);
        UpdateWizardControls();
        if (_showingDownloadSetup) Steamy.Controls.EntranceMotion.Reveal(step == 0 ? SourceStepPanel : step == 1 ? DepotStepPanel : LocationStepPanel);
    }

    private void UpdateWizardControls()
    {
        WizardBusyBar.Visibility = _wizardBusy ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _showingDownloadSetup && _wizardStep > 0 && !_resumingExisting ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = !_downloadRunning && !_wizardBusy;
        NewSelectionButton.Visibility = _showingDownloadSetup && _resumingExisting ? Visibility.Visible : Visibility.Collapsed;
        NewSelectionButton.IsEnabled = !_downloadRunning && !_wizardBusy;
        var canChangeSource = !_downloadRunning && !_resumingExisting && !_wizardBusy;
        SourceStepPanel.IsEnabled = canChangeSource;
        // The source page can be collapsed/unrealized during Resume. Keep each
        // control's state explicit rather than relying on a hidden scroll parent's coercion.
        SourceLocal.IsEnabled = BrowseLocalPackage.IsEnabled = SourceSushi.IsEnabled = SourceZaza.IsEnabled = SourceRyuu.IsEnabled = SourceHubcap.IsEnabled = SourceDepotBox.IsEnabled = canChangeSource;
        DepotStepPanel.IsEnabled = !_downloadRunning && !_wizardBusy;
        LocationBrowseButton.IsEnabled = !_downloadRunning && !_resumingExisting;
        StartButton.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = _showingDownloadSetup && _wizardStep < 2 && !_resumingExisting
            ? Wpf.Ui.Controls.SymbolRegular.ChevronRight24 : Wpf.Ui.Controls.SymbolRegular.ArrowDownload24 };
        StartButton.Content = !_showingDownloadSetup ? (_resumingExisting ? "Resume download" : "Download") : _downloadCompleted ? "Completed" : _wizardBusy ? "Loading depots…" : _resumingExisting ? "Resume download" : _wizardStep == 2 ? "Download" : "Next";
        StartButton.IsEnabled = !_downloadCompleted && !_downloadRunning && !_wizardBusy && (!_showingDownloadSetup || _resumingExisting
            || (_wizardStep == 0 ? !_sourceUnavailable && (_selectedSource != ManifestSource.Local || !string.IsNullOrWhiteSpace(_localPackagePath)) : _wizardStep == 1 ? ValidDepotSelection() : ValidDepotSelection() && ValidLocation(_downloadPath)));
        var selected = _depotChoices.Where(choice => choice.IsSelected).ToArray();
        var knownBytes = SelectedSizeBytes();
        DepotSelectionSummary.Text = $"{selected.Length} of {_depotChoices.Count} depots selected"
            + (knownBytes is { } bytes ? $" · {DownloadFormat.Bytes(bytes)} reported size" : " · size unavailable");
        if (!_resumingExisting) LocationSelectionText.Text = $"{_selectedSource} · {selected.Length} selected depot{(selected.Length == 1 ? "" : "s")} · selected versions";
    }

    private bool ValidDepotSelection() => _preparedDownload is not null && _depotChoices.Any(choice => choice.IsSelected)
        && _depotChoices.Where(choice => choice.IsSelected).All(choice => choice.SelectedVersion is not null);

    private long? SelectedSizeBytes()
    {
        var selected = _depotChoices.Where(choice => choice.IsSelected).ToArray();
        if (selected.Length == 0 || selected.Any(choice => choice.SelectedVersion?.SizeBytes is not > 0)) return null;
        try { return selected.Aggregate(0L, (total, choice) => checked(total + choice.SelectedVersion!.SizeBytes!.Value)); }
        catch (OverflowException) { return null; }
    }

    private static bool ValidLocation(string folder)
    {
        try { return !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Path.GetFullPath(folder).Length > 0; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }

    public void ConfigureDownloadLocation(string baseFolder)
    {
        if (_resumingExisting && _resumeJob is not null)
            baseFolder = Path.GetDirectoryName(_resumeJob.TargetFolder) ?? _downloadPath;
        _downloadPath = baseFolder;
        DownloadPathText.Text = string.IsNullOrWhiteSpace(baseFolder) ? "Select a folder…" : baseFolder;
        DownloadPathText.Foreground = ValidLocation(baseFolder) ? PrimaryText : TertiaryText;
        DownloadTargetText.Text = _resumeJob?.TargetFolder ?? (_selectedItem is null || !ValidLocation(baseFolder)
            ? "Choose an absolute folder path." : TargetFolderFor(_selectedItem, App.Services.GetRequiredService<ISettingsService>().Load(), baseFolder));
        UpdateWizardControls();
        _ = UpdateLocationSpaceAsync();
    }

    private async Task UpdateLocationSpaceAsync()
    {
        var revision = ++_locationRevision;
        var target = DownloadTargetText.Text;
        if (!ValidLocation(_downloadPath)) { LocationSpaceText.Text = "Choose a valid folder before starting."; return; }
        LocationSpaceText.Text = "Checking available space…";
        var space = await Task.Run(() => AvailableBytes(target));
        if (revision != _locationRevision) return;
        var required = SelectedSizeBytes();
        LocationSpaceText.Text = space is { } bytes ? $"{DownloadFormat.Bytes(bytes)} free on the target drive"
            + (required is { } size ? $" · {DownloadFormat.Bytes(size)} selected" : " · download size not supplied") : "Available space could not be checked.";
    }

    private static long? AvailableBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private void DepotChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_batchDepotSelection) return;
        UpdateWizardControls();
        if (_wizardStep == 2) _ = UpdateLocationSpaceAsync();
    }

    private void SelectAllDepots_Click(object sender, RoutedEventArgs args) => SetAllDepotsSelected(true);
    private void ClearDepotSelection_Click(object sender, RoutedEventArgs args) => SetAllDepotsSelected(false);
    private void SetAllDepotsSelected(bool selected)
    {
        _batchDepotSelection = true;
        try { foreach (var choice in _depotChoices) choice.IsSelected = selected; }
        finally { _batchDepotSelection = false; }
        UpdateWizardControls();
    }

    private void WizardBackButton_Click(object sender, RoutedEventArgs args)
    {
        if (_downloadRunning || _wizardBusy || _resumingExisting) return;
        if (_wizardStep == 0 && _selectedItem is { } item) OpenGameDetails(item);
        else SetWizardStep(_wizardStep - 1);
    }

    private void ResetPreparedDownload()
    {
        _preparationCts?.Cancel();
        if (_preparedDownload is { } plan)
        {
            try { App.Services.GetRequiredService<IRyuuGameDownloadService>().DiscardPreparedDownload(plan.Id); }
            catch (Exception exception) { App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Debug, "Download wizard", $"Snapshot cleanup: {exception.GetType().Name}"); }
        }
        _preparedDownload = null;
        foreach (var choice in _depotChoices) choice.PropertyChanged -= DepotChoiceChanged;
        _depotChoices.Clear();
        _wizardBusy = false;
    }

    public async Task AdvanceDownloadWizardAsync(bool navigateToDownloads = false)
    {
        if (_selectedItem is null || _downloadRunning || _wizardBusy) return;
        if (!_showingDownloadSetup) { OpenDownloadSetup(_selectedItem); return; }
        if (_resumingExisting) { await StartDownloadAsync(navigateToDownloads); return; }
        if (_wizardStep == 2)
        {
            if (!ValidDepotSelection() || !ValidLocation(_downloadPath)) { OverlayStatus.Text = "Choose at least one depot version and a valid download folder."; return; }
            await StartDownloadAsync(navigateToDownloads);
            return;
        }
        if (_wizardStep == 1)
        {
            if (!ValidDepotSelection()) { OverlayStatus.Text = "Select at least one depot and its version to continue."; return; }
            SetWizardStep(2);
            OverlayStatus.Text = "Review the selection and choose where to save the game.";
            return;
        }
        if (_sourceUnavailable) { OverlayStatus.Text = "This source is unavailable. Choose another source or configure its credentials in Settings."; return; }
        if (_preparedDownload is { } existing && existing.AppId == _selectedItem.AppId && existing.Source == _selectedSource)
        { SetWizardStep(1); return; }
        _availabilityCts?.Cancel();
        _preparationCts?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _preparationCts = cancellation;
        _wizardBusy = true;
        UpdateWizardControls();
        var item = _selectedItem;
        var source = _selectedSource;
        var downloader = App.Services.GetRequiredService<IRyuuGameDownloadService>();
        OverlayStatus.Text = $"Loading depot versions from {source}…";
        try
        {
            using var progress = new Steamy.Controls.BufferedDownloadProgress(message =>
            {
                if (!cancellation.IsCancellationRequested && ReferenceEquals(_selectedItem, item) && _selectedSource == source)
                    OverlayStatus.Text = message;
            });
            var packagePath = _localPackagePath;
            var preparation = await Task.Run(() => source == ManifestSource.Local
                ? downloader.PrepareLocalPackageAsync(item.AppId, packagePath ?? "", progress, cancellation.Token)
                : downloader.PrepareDownloadAsync(item.AppId, source, progress, cancellation.Token));
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_selectedItem, item) || _selectedSource != source)
            {
                if (preparation.Plan is not null) downloader.DiscardPreparedDownload(preparation.Plan.Id);
                return;
            }
            if (!preparation.Succeeded || preparation.Plan is not { Depots.Count: > 0 } plan)
            {
                if (preparation.Plan is not null) downloader.DiscardPreparedDownload(preparation.Plan.Id);
                OverlayStatus.Text = preparation.Message;
                return;
            }
            _preparedDownload = plan;
            foreach (var depot in plan.Depots)
            {
                var choice = new DownloadDepotChoice(depot);
                choice.PropertyChanged += DepotChoiceChanged;
                _depotChoices.Add(choice);
            }
            DepotSourceText.Text = $"{source} · {plan.Depots.Count} available depots";
            SetWizardStep(1);
            OverlayStatus.Text = "Select the depots and manifest versions you want to download.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested) OverlayStatus.Text = $"Could not load depot versions: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_preparationCts, cancellation))
            {
                _preparationCts = null;
                _wizardBusy = false;
                UpdateWizardControls();
            }
        }
    }

    private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AdvanceDownloadWizardAsync(navigateToDownloads: true);
        }
        catch (Exception exception)
        {
            OverlayStatus.Text = $"Error: {exception.Message}";
        }
    }

    /// <summary>Pause asks the running download to stop; Resume later continues in the same folder.</summary>
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pauseCurrentDownload is null) return;
        _pauseCurrentDownload();
        PauseButton.IsEnabled = false;
        OverlayStatus.Text = "Pausing…";
    }

    private async Task StartDownloadAsync(bool navigateToDownloads)
    {
        if (_selectedItem is null) return;
        // A second click on Start/Resume while the download runs must not spawn a second
        // DepotDownloaderMod over the same files.
        if (_downloadRunning) return;
        _downloadRunning = true;
        var revision = ++_downloadRevision;
        try
        {
            await RunDownloadAsync(navigateToDownloads);
        }
        finally
        {
            if (revision == _downloadRevision)
            {
                _downloadRunning = false;
                if (_selectedItem is not null) UpdateWizardControls();
            }
        }
    }

    private async Task RunDownloadAsync(bool navigateToDownloads)
    {
        if (_selectedItem is null) return;

        var item = _selectedItem;
        var operationRevision = _downloadRevision;
        var resumingExisting = _resumingExisting;
        var resumeJob = _resumeJob;
        var selectedSource = _selectedSource;
        var archivePath = _customArchivePath;
        bool IsCurrentDialog() => operationRevision == _downloadRevision && ReferenceEquals(_selectedItem, item);
        var settings = App.Services.GetRequiredService<ISettingsService>().Load();

        var targetFolder = resumeJob?.TargetFolder ?? TargetFolderFor(item, settings, _downloadPath);
        if (!ValidLocation(targetFolder)) { OverlayStatus.Text = "Choose a valid absolute download folder."; return; }
        targetFolder = Path.GetFullPath(targetFolder);
        var prepared = _preparedDownload;
        var reservation = targetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!ReservedTargets.TryAdd(reservation, 0))
        {
            OverlayStatus.Text = "A download is already starting or running in this folder. Open Downloads or choose another folder.";
            return;
        }
        try
        {
        var selections = _depotChoices.Where(choice => choice.IsSelected && choice.SelectedVersion is not null)
            .Select(choice => new CachedDepotManifest(choice.DepotId, choice.SelectedVersion!.ManifestId)).ToArray();

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
        if (!resumingExisting && store.Downloads.Any(existing => existing.AppId == item.AppId
            && string.Equals(existing.TargetFolder, targetFolder, StringComparison.OrdinalIgnoreCase)
            && existing.State is DownloadJobState.Queued or DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled))
        {
            OverlayStatus.Text = "This folder already has a saved download. Open Downloads to continue its saved versions, or choose another folder.";
            return;
        }
        if (!resumingExisting && (prepared is null || prepared.AppId != item.AppId || prepared.Source != selectedSource || selections.Length == 0))
        { OverlayStatus.Text = "Load and select depot versions before starting the download."; return; }
        if (!resumingExisting && SelectedSizeBytes() is { } required)
        {
            var available = await Task.Run(() => AvailableBytes(targetFolder));
            if (available is { } bytes && bytes < required)
            {
                if (IsCurrentDialog()) OverlayStatus.Text = $"Not enough free space: {DownloadFormat.Bytes(required)} selected, {DownloadFormat.Bytes(bytes)} available. Choose another drive or fewer depots.";
                return;
            }
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
            DownloadMode = $"DepotDownloaderMod ({selectedSource})",
            AuthorizationConfirmed = true
        };
        var downloadManager = App.Services.GetRequiredService<IDownloadManager>();
        using var cts = new CancellationTokenSource();
        try { downloadManager.RegisterJob(job.Id, cts); }
        catch (InvalidOperationException)
        {
            if (IsCurrentDialog()) OverlayStatus.Text = "This download is still finishing its previous operation. Please try again shortly.";
            return;
        }
        var initialPersisted = false;
        try
        {
        var previousState = job.State;
        var previousStatus = job.Status;
        var previousFinished = job.Finished;
        var isResume = job.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Cancelled;
        if (isResume)
        {
            // A resume belongs to its original source and manifest snapshot.
            job.Finished = null;
        }
        job.State = DownloadJobState.Preparing;
        job.Status = isResume ? $"Resuming download from {selectedSource}..." : $"Starting download from {selectedSource}...";

        try
        {
            await queueStore.SaveAsync(job);
            initialPersisted = true;
        }
        catch (Exception exception)
        {
            job.State = previousState;
            job.Status = previousStatus;
            job.Finished = previousFinished;
            if (IsCurrentDialog()) OverlayStatus.Text = "The download could not be saved. Check access to the Steamy data folder and try again. No download was started.";
            App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Error, "Download wizard", $"Queue save failed before starting: {exception.Message}", job.AppId, job.Id);
            return;
        }
        if (!store.Downloads.Contains(job)) store.Downloads.Insert(0, job);
        if (navigateToDownloads && IsCurrentDialog())
        {
            // The app-level manager owns the transfer after the initial save succeeds.
            ResetOverlay();
            try { App.Services.GetRequiredService<INavigationService>().Navigate<DownloadsPage>(); }
            catch (Exception exception)
            { App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Warning, "Downloads", $"Could not open the queue: {exception.Message}"); }
        }

        if (IsCurrentDialog())
        {
            StartButton.IsEnabled = false;
            StartButton.Content = "Downloading...";
            PauseButton.Visibility = Visibility.Visible;
            PauseButton.IsEnabled = true;
            OverlayStatus.Text = job.Status;
        }

        var ryuuService = App.Services.GetRequiredService<IRyuuGameDownloadService>();
        var pauseRequested = false;
        if (IsCurrentDialog())
        {
            _downloadCts = cts;
            _pauseCurrentDownload = () => { pauseRequested = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        }

        var source = selectedSource;
        if (isResume)
        {
            foreach (var name in Enum.GetNames<ManifestSource>())
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

            if (IsCurrentDialog())
                OverlayStatus.Text = job.IsActive ? $"{job.Status} — {job.ProgressLabel}" : job.Status;
        });

        try
        {
            // Resume continues from the cached manifests — no refresh first, otherwise the source
            // hands out newer manifests and DepotDownloader re-downloads everything.
            var result = isResume
                ? await Task.Run(() => ryuuService.ResumeDownloadAsync(item.AppId, targetFolder, progress, cts.Token))
                : await Task.Run(() => ryuuService.DownloadPreparedAsync(prepared!, selections, targetFolder, progress, cts.Token));
            token.ThrowIfCancellationRequested();
            if (result.Succeeded && !string.IsNullOrEmpty(archivePath) && File.Exists(archivePath))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    token.ThrowIfCancellationRequested();
                    job.Status = "Extracting custom archive...";
                    if (IsCurrentDialog()) OverlayStatus.Text = job.Status;
                });
                await Task.Run(() => { token.ThrowIfCancellationRequested(); ExtractArchive(archivePath, targetFolder, token); }, token);
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

                if (IsCurrentDialog())
                {
                    if (!result.Succeeded)
                    {
                        _resumeJob = job;
                        _resumingExisting = true;
                        SetWizardStep(2);
                    }
                    OverlayStatus.Text = result.Succeeded
                        ? $"Done! {result.Message}"
                        : $"Failed: {result.Message}";
                    _downloadCompleted = result.Succeeded;
                    StartButton.Content = result.Succeeded ? "Completed" : "Retry";
                    StartButton.IsEnabled = !result.Succeeded;
                }
            });
        }
        catch (OperationCanceledException)
            {
                // The app-level pause flag wins over a plain cancel: both arrive as an
                // OperationCanceledException here, so the flag tells the two apart.
                var wasPaused = pauseRequested || downloadManager.IsPauseRequested(job.Id);
                job.State = wasPaused ? DownloadJobState.Paused : DownloadJobState.Cancelled;
                job.Status = wasPaused ? "Paused — resume will continue from existing files" : "Download cancelled.";
                job.ClearLiveStats();
                if (IsCurrentDialog())
                {
                    _resumeJob = job;
                    _resumingExisting = true;
                    SetWizardStep(2);
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
                if (IsCurrentDialog())
                {
                    _resumeJob = job;
                    _resumingExisting = true;
                    SetWizardStep(2);
                    OverlayStatus.Text = $"Error: {ex.Message}";
                    StartButton.Content = "Retry";
                    StartButton.IsEnabled = true;
                }
            }
        }
        finally
        {
                try
                {
                    if (initialPersisted) await queueStore.SaveAsync(job);
                }
                catch (Exception exception)
                {
                    job.Status += " · Queue state could not be saved; check the Steamy data folder.";
                    App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Error, "Download wizard", $"Final queue save failed: {exception.Message}", job.AppId, job.Id);
                    if (IsCurrentDialog()) OverlayStatus.Text = job.Status;
                }
                finally
                {
                    downloadManager.UnregisterJob(job.Id);
                    if (IsCurrentDialog())
                    {
                        _downloadCts = null;
                        _pauseCurrentDownload = null;
                        PauseButton.Visibility = Visibility.Collapsed;
                    }
                    if (prepared is not null && (initialPersisted || !IsCurrentDialog()))
                    {
                        App.Services.GetRequiredService<IRyuuGameDownloadService>().DiscardPreparedDownload(prepared.Id);
                        if (IsCurrentDialog() && ReferenceEquals(_preparedDownload, prepared)) _preparedDownload = null;
                    }
                }
            }
        }
        finally
        {
            ReservedTargets.TryRemove(reservation, out _);
            if (!IsCurrentDialog() && prepared is not null)
                App.Services.GetRequiredService<IRyuuGameDownloadService>().DiscardPreparedDownload(prepared.Id);
        }
    }

    private static string TargetFolderFor(SteamCatalogItem item, AppSettings settings, string selectedFolder)
    {
        var folder = string.IsNullOrWhiteSpace(selectedFolder) ? settings.DownloadFolder : selectedFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames");
        return Path.Combine(folder, SanitizeFolderName(item.Name, item.AppId));
    }

    private static string SanitizeFolderName(string name, int appId)
    {
        var safe = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1F]", "_").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") return $"App {appId}";
        var firstPart = safe.Split('.')[0];
        if (Regex.IsMatch(firstPart, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) safe = "_" + safe;
        return safe.Length <= 120 ? safe : safe[..120].TrimEnd('.', ' ');
    }
}

public sealed class DownloadDepotChoice : UiObservableObject
{
    private bool _isSelected = true;
    private PreparedDepotVersion? _selectedVersion;
    public int DepotId { get; }
    public string Name { get; }
    public IReadOnlyList<PreparedDepotVersion> Versions { get; }
    public string MetadataSummary { get; }
    public string MetadataSource { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public PreparedDepotVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(SizeLabel));
                OnPropertyChanged(nameof(BranchLabel));
            }
        }
    }
    public string SizeLabel => string.Join(" · ", new[]
    {
        SelectedVersion?.SizeBytes is > 0 ? DownloadFormat.Bytes(SelectedVersion.SizeBytes.Value) + " installed" : "Installed size unavailable",
        SelectedVersion?.CompressedSizeBytes is > 0 ? DownloadFormat.Bytes(SelectedVersion.CompressedSizeBytes.Value) + " download" : null
    }.Where(value => !string.IsNullOrEmpty(value)));
    public string BranchLabel => string.IsNullOrWhiteSpace(SelectedVersion?.BranchName) ? "" : SelectedVersion.BranchName;
    public DownloadDepotChoice(PreparedDownloadDepot depot)
    {
        DepotId = depot.DepotId;
        Name = depot.Name;
        MetadataSummary = string.Join(" · ", new[] { depot.ContentType, depot.OperatingSystems, depot.Languages,
            depot.DlcAppId is > 0 ? $"DLC {depot.DlcAppId}" : null, depot.SharedAppId is > 0 ? $"Shared with app {depot.SharedAppId}" : null }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        MetadataSource = depot.MetadataSource ?? string.Empty;
        Versions = depot.Versions;
        _selectedVersion = Versions.FirstOrDefault(version => version.ManifestId == depot.DefaultManifestId) ?? Versions.FirstOrDefault();
    }
}
