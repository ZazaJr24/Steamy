using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class GameFixesPage : Page
{
    private GameFixGameCard? _selectedCard;
    private string _folderPath = string.Empty;
    private bool _isOverlayClosing;
    private bool _isApplying;

    private Brush PrimaryText => ThemeBrush("TextPrimaryBrush");
    private Brush TertiaryText => ThemeBrush("TextTertiaryBrush");
    private Brush ThemeBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    public GameFixesPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GameFixesViewModel>();
    }

    private async void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not GameFixGameCard card || card.Game is null) return;

        _selectedCard = card;
        _folderPath = string.Empty;
        _isApplying = false;
        OverlayTitle.Text = card.Name;
        OverlayAppId.Text = $"App {card.AppId}  ·  {card.FixCount} fix(es)";
        SetStatus("Select the game folder, then download and apply fixes.", StatusKind.Info);
        FolderPathText.Text = "Select the game folder…";
        FolderPathText.Foreground = TertiaryText;
        ApplyAllButton.Content = "Download & Apply All";
        ApplyAllButton.IsEnabled = true;

        LoadHeroImage(card.AppId);
        BuildFixRows(card.Game);

        MainContentGrid.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
        await ShowOverlayAsync();
    }

    private void LoadHeroImage(string appId)
    {
        OverlayHeroImage.Source = null;
        if (string.IsNullOrWhiteSpace(appId)) return;
        try
        {
            var uri = new Uri($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 280;
            bmp.EndInit();
            OverlayHeroImage.Source = bmp;
        }
        catch { }
    }

    private enum StatusKind { Info, Success, Error }

    private void SetStatus(string message, StatusKind kind)
    {
        OverlayStatus.Text = message;
        StatusBorder.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Symbol = kind switch
        {
            StatusKind.Success => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
            StatusKind.Error => Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
            _ => Wpf.Ui.Controls.SymbolRegular.Info24
        };
    }

    private void BuildFixRows(FixGame game)
    {
        FixesList.Children.Clear();
        foreach (var fix in game.Fixes)
        {
            var row = new Border { Style = (Style)FindResource("FixRow") };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBadge = new Border { Style = (Style)FindResource("FixIconBadge") };
            iconBadge.Child = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24,
                FontSize = 18,
                Foreground = (Brush)FindResource("AccentBrush")
            };
            Grid.SetColumn(iconBadge, 0);
            grid.Children.Add(iconBadge);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = fix.Filename,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var metaPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (!string.IsNullOrWhiteSpace(fix.Size))
            {
                metaPanel.Children.Add(new TextBlock
                {
                    Text = fix.Size,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    FontSize = 11
                });
            }
            if (fix.Badges.Count > 0)
            {
                foreach (var badge in fix.Badges)
                {
                    if (metaPanel.Children.Count > 0)
                        metaPanel.Children.Add(new TextBlock
                        {
                            Text = " · ",
                            Foreground = (Brush)FindResource("TextTertiaryBrush"),
                            FontSize = 11
                        });

                    var b = new Border { Style = (Style)FindResource("FixBadge") };
                    b.Child = new TextBlock
                    {
                        Text = badge,
                        Foreground = (Brush)FindResource("AccentBrush"),
                        FontSize = 10.5, FontWeight = FontWeights.SemiBold
                    };
                    metaPanel.Children.Add(b);
                }
            }
            if (metaPanel.Children.Count > 0)
                info.Children.Add(metaPanel);

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var applyBtn = new Wpf.Ui.Controls.Button
            {
                Content = "Apply",
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Play24 },
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Tag = fix
            };
            applyBtn.Click += ApplySingleFix_Click;
            Grid.SetColumn(applyBtn, 2);
            grid.Children.Add(applyBtn);

            row.Child = grid;
            FixesList.Children.Add(row);
        }
    }

    private async void ApplySingleFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not FixEntry fix) return;
        if (string.IsNullOrWhiteSpace(_folderPath)) { SetStatus("Select the game folder first.", StatusKind.Error); return; }
        if (_isApplying) return;

        _isApplying = true;
        btn.IsEnabled = false;
        btn.Content = "Downloading…";
        try
        {
            await DownloadAndApplyFixAsync(fix);
            btn.Content = "Applied";
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", StatusKind.Error);
            btn.Content = "Retry";
            btn.IsEnabled = true;
        }
        finally { _isApplying = false; }
    }

    private async void ApplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.Game is null) return;
        if (string.IsNullOrWhiteSpace(_folderPath)) { SetStatus("Select the game folder first.", StatusKind.Error); return; }
        if (_isApplying) return;

        _isApplying = true;
        ApplyAllButton.IsEnabled = false;
        ApplyAllButton.Content = "Downloading…";
        try
        {
            foreach (var fix in _selectedCard.Game.Fixes)
                await DownloadAndApplyFixAsync(fix);
            SetStatus($"Done! All fixes applied to {_folderPath}.", StatusKind.Success);
            ApplyAllButton.Content = "Applied";
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", StatusKind.Error);
            ApplyAllButton.Content = "Retry";
            ApplyAllButton.IsEnabled = true;
        }
        finally { _isApplying = false; }
    }

    private async Task DownloadAndApplyFixAsync(FixEntry fix)
    {
        var downloadService = App.Services.GetRequiredService<IGameFixDownloadService>();
        var logging = App.Services.GetRequiredService<ILoggingService>();
        var settings = App.Services.GetRequiredService<ISettingsService>().Load();
        var gameName = _selectedCard?.Name ?? "Unknown";
        var appId = 0;
        if (_selectedCard is not null)
            int.TryParse(_selectedCard.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out appId);

        var source = FixSource.Resolve(settings.FixMirrorUrl)
            ?? throw new InvalidOperationException("The fixes source URL in Settings is not valid. Clear it to use the built-in source.");
        var token = await App.Services.GetRequiredService<ISecureCredentialService>().ReadAsync(FixSource.TokenCredentialName);

        SetStatus($"Downloading {fix.Filename}…", StatusKind.Info);
        var dlProgress = new Progress<GameFixDownloadProgress>(p =>
            Dispatcher.BeginInvoke(() => SetStatus($"Downloading {fix.Filename}… {p.Downloaded} / {p.Total} ({p.Percent:F0}%)", StatusKind.Info)));
        var dlResult = await downloadService.DownloadAsync(source.FileUrl(fix), fix.Filename, gameName, appId, dlProgress, default, token);

        if (!dlResult.Succeeded)
        {
            SetStatus($"Download failed: {dlResult.Message}. Use \"Apply ZIP\" to select it manually.", StatusKind.Error);
            throw new InvalidOperationException(dlResult.Message);
        }

        SetStatus($"Extracting {fix.Filename} into {_folderPath}…", StatusKind.Info);
        var applyProgress = new Progress<string>(m => Dispatcher.BeginInvoke(() => SetStatus(m, StatusKind.Info)));
        var result = await downloadService.ApplyArchiveAsync(dlResult.LocalPath, _folderPath, applyProgress);

        if (!result.Succeeded)
        {
            SetStatus($"Extract failed: {result.Message}", StatusKind.Error);
            throw new InvalidOperationException(result.Message);
        }

        SetStatus($"Applied {fix.Filename} — {result.ExtractedSize}", StatusKind.Success);
        logging.Add(LogLevel.Info, "GameFixes", $"Applied {fix.Filename} for {gameName} (App {appId}) into {_folderPath}. Size: {result.ExtractedSize}", appId);
    }

    private async void ApplyZipButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folderPath)) { SetStatus("Select the game folder first.", StatusKind.Error); return; }

        var dialog = new OpenFileDialog
        {
            Title = "Select the downloaded fix ZIP",
            Filter = "ZIP archives|*.zip|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!File.Exists(dialog.FileName)) return;

        _isApplying = true;
        SetStatus($"Extracting into {_folderPath}…", StatusKind.Info);
        try
        {
            var applyService = App.Services.GetRequiredService<IGameFixDownloadService>();
            var logging = App.Services.GetRequiredService<ILoggingService>();
            var gameName = _selectedCard?.Name ?? "Unknown";
            var appId = 0;
            if (_selectedCard is not null)
                int.TryParse(_selectedCard.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out appId);

            var progress = new Progress<string>(m => Dispatcher.BeginInvoke(() => SetStatus(m, StatusKind.Info)));
            var result = await applyService.ApplyArchiveAsync(dialog.FileName, _folderPath, progress);

            if (result.Succeeded)
                SetStatus($"Done! Extracted {result.ExtractedSize} into {_folderPath}.", StatusKind.Success);
            else
                SetStatus($"Extract failed: {result.Message}", StatusKind.Error);

            if (result.Succeeded)
                logging.Add(LogLevel.Info, "GameFixes", $"Applied fix for {gameName} (App {appId}) into {_folderPath}. Size: {result.ExtractedSize}", appId);
        }
        catch (Exception ex) { SetStatus($"Failed: {ex.Message}", StatusKind.Error); }
        finally { _isApplying = false; }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var title = _selectedCard is not null ? $"Select {_selectedCard.Name} folder" : "Select game folder";
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false, ValidateNames = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _folderPath = dialog.FolderName;
            FolderPathText.Text = _folderPath;
            FolderPathText.Foreground = PrimaryText;
            SetStatus($"Folder selected: {_folderPath}", StatusKind.Success);
        }
    }

    private void Overlay_Close(object sender, MouseButtonEventArgs e) => _ = CloseOverlayAsync();
    private void OverlayCloseButton_Click(object sender, RoutedEventArgs e) => _ = CloseOverlayAsync();

    private async Task ShowOverlayAsync()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DialogScale.ScaleX = DialogScale.ScaleY = 0.94;
        DialogPanel.Opacity = 0;
        OverlayGrid.Opacity = 0;
        OverlayGrid.Visibility = Visibility.Visible;
        OverlayGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        DialogPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        await Task.Delay(230);
    }

    private async Task CloseOverlayAsync()
    {
        if (_isOverlayClosing || OverlayGrid.Visibility != Visibility.Visible) return;
        _isOverlayClosing = true;
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var close = TimeSpan.FromMilliseconds(150);
            OverlayGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, close) { EasingFunction = ease });
            DialogPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, close) { EasingFunction = ease });
            DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, close) { EasingFunction = ease });
            DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, close) { EasingFunction = ease });
            await Task.Delay(165);
            OverlayGrid.BeginAnimation(UIElement.OpacityProperty, null);
            DialogPanel.BeginAnimation(UIElement.OpacityProperty, null);
            DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            OverlayGrid.Visibility = Visibility.Collapsed;
            MainContentGrid.Effect = null;
            _selectedCard = null;
        }
        finally { _isOverlayClosing = false; }
    }

}
