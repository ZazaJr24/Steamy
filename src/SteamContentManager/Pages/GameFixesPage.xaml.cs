using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        OverlayTitle.Text = card.Name;
        OverlayAppId.Text = $"App {card.AppId}  ·  {card.FixCount} fix(es)";
        OverlayStatus.Text = "Select the game folder, then download and apply fixes.";
        FolderPathText.Text = "Select the game folder…";
        FolderPathText.Foreground = TertiaryText;

        BuildFixRows(card.Game);

        MainContentGrid.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
        await ShowOverlayAsync();
    }

    private void BuildFixRows(RyuuFixGame game)
    {
        FixesList.Children.Clear();
        foreach (var fix in game.Fixes)
        {
            var row = new Border { Style = (Style)FindResource("FixRow") };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = fix.Filename,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = fix.Size,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 11, Margin = new Thickness(0, 3, 0, 0)
            });

            if (fix.Badges.Count > 0)
            {
                var badgePanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var badge in fix.Badges)
                {
                    var b = new Border { Style = (Style)FindResource("FixBadge") };
                    b.Child = new TextBlock
                    {
                        Text = badge, Foreground = (Brush)FindResource("AccentBrush"),
                        FontSize = 10.5, FontWeight = FontWeights.SemiBold
                    };
                    badgePanel.Children.Add(b);
                }
                info.Children.Add(badgePanel);
            }

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var applyBtn = new Wpf.Ui.Controls.Button
            {
                Content = "Apply",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Tag = fix
            };
            applyBtn.Click += ApplySingleFix_Click;
            Grid.SetColumn(applyBtn, 1);
            grid.Children.Add(applyBtn);

            row.Child = grid;
            FixesList.Children.Add(row);
        }
    }

    private async void ApplySingleFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not RyuuFixEntry fix) return;
        if (string.IsNullOrWhiteSpace(_folderPath)) { OverlayStatus.Text = "Select the game folder first."; return; }
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
            OverlayStatus.Text = $"Failed: {ex.Message}";
            btn.Content = "Retry";
            btn.IsEnabled = true;
        }
        finally { _isApplying = false; }
    }

    private async void ApplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.Game is null) return;
        if (string.IsNullOrWhiteSpace(_folderPath)) { OverlayStatus.Text = "Select the game folder first."; return; }
        if (_isApplying) return;

        _isApplying = true;
        ApplyAllButton.IsEnabled = false;
        ApplyAllButton.Content = "Downloading…";
        try
        {
            foreach (var fix in _selectedCard.Game.Fixes)
                await DownloadAndApplyFixAsync(fix);
            OverlayStatus.Text = $"Done! All fixes applied to {_folderPath}.";
            ApplyAllButton.Content = "Applied";
        }
        catch (Exception ex)
        {
            OverlayStatus.Text = $"Failed: {ex.Message}";
            ApplyAllButton.Content = "Retry";
            ApplyAllButton.IsEnabled = true;
        }
        finally { _isApplying = false; }
    }

    private async Task DownloadAndApplyFixAsync(RyuuFixEntry fix)
    {
        var downloadService = App.Services.GetRequiredService<IGameFixDownloadService>();
        var logging = App.Services.GetRequiredService<ILoggingService>();
        var settings = App.Services.GetRequiredService<ISettingsService>().Load();
        var gameName = _selectedCard?.Name ?? "Unknown";
        var appId = 0;
        if (_selectedCard is not null)
            int.TryParse(_selectedCard.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out appId);

        var authCode = settings.RyuuApiKey;
        if (string.IsNullOrWhiteSpace(authCode))
        {
            var credentials = App.Services.GetRequiredService<ISecureCredentialService>();
            authCode = await credentials.ReadAsync("ryuu-auth-key") ?? string.Empty;
        }

        var url = fix.Href;
        if (!string.IsNullOrWhiteSpace(authCode))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url = $"{url}{separator}auth_code={Uri.EscapeDataString(authCode)}";
        }

        OverlayStatus.Text = $"Downloading {fix.Filename}…";
        var dlProgress = new Progress<GameFixDownloadProgress>(p =>
            Dispatcher.BeginInvoke(() => OverlayStatus.Text = $"Downloading {fix.Filename}… {p.Downloaded} / {p.Total} ({p.Percent:F0}%)"));
        var dlResult = await downloadService.DownloadAsync(url, fix.Filename, gameName, appId, dlProgress);

        if (!dlResult.Succeeded)
        {
            OverlayStatus.Text = $"Download failed: {dlResult.Message}. Use \"Apply ZIP\" to select it manually.";
            throw new InvalidOperationException(dlResult.Message);
        }

        OverlayStatus.Text = $"Extracting {fix.Filename} into {_folderPath}…";
        var applyProgress = new Progress<string>(m => Dispatcher.BeginInvoke(() => OverlayStatus.Text = m));
        var result = await downloadService.ApplyArchiveAsync(dlResult.LocalPath, _folderPath, applyProgress);

        if (!result.Succeeded)
        {
            OverlayStatus.Text = $"Extract failed: {result.Message}";
            throw new InvalidOperationException(result.Message);
        }

        OverlayStatus.Text = $"Applied {fix.Filename} — {result.ExtractedSize}";
        logging.Add(LogLevel.Info, "GameFixes", $"Applied {fix.Filename} for {gameName} (App {appId}) into {_folderPath}. Size: {result.ExtractedSize}", appId);
    }

    private async void ApplyZipButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folderPath)) { OverlayStatus.Text = "Select the game folder first."; return; }

        var dialog = new OpenFileDialog
        {
            Title = "Select the downloaded fix ZIP",
            Filter = "ZIP archives|*.zip|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!File.Exists(dialog.FileName)) return;

        _isApplying = true;
        OverlayStatus.Text = $"Extracting into {_folderPath}…";
        try
        {
            var applyService = App.Services.GetRequiredService<IGameFixDownloadService>();
            var logging = App.Services.GetRequiredService<ILoggingService>();
            var gameName = _selectedCard?.Name ?? "Unknown";
            var appId = 0;
            if (_selectedCard is not null)
                int.TryParse(_selectedCard.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out appId);

            var progress = new Progress<string>(m => Dispatcher.BeginInvoke(() => OverlayStatus.Text = m));
            var result = await applyService.ApplyArchiveAsync(dialog.FileName, _folderPath, progress);

            OverlayStatus.Text = result.Succeeded
                ? $"Done! Extracted {result.ExtractedSize} into {_folderPath}."
                : $"Extract failed: {result.Message}";

            if (result.Succeeded)
                logging.Add(LogLevel.Info, "GameFixes", $"Applied fix for {gameName} (App {appId}) into {_folderPath}. Size: {result.ExtractedSize}", appId);
        }
        catch (Exception ex) { OverlayStatus.Text = $"Failed: {ex.Message}"; }
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
            OverlayStatus.Text = $"Folder selected: {_folderPath}";
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
