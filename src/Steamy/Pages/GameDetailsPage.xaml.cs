using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Controls;

namespace Steamy.Pages;

public partial class GameDetailsPage : Page
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private GameDetailsViewModel Model => (GameDetailsViewModel)DataContext;
    private IShellBackdropService? _backdrop;
    private Window? _owner;
    private LibraryPage? _installationSheet;
    public LibraryPage? InstallationSheet => _installationSheet;
    public bool IsInstallationOpen => InstallationHost.Visibility == Visibility.Visible;
    public GameDetailsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GameDetailsViewModel>();
        Loaded += async (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner is not null) _owner.SizeChanged += OwnerSizeChanged;
            _backdrop = Window.GetWindow(this) is MainWindow ? App.Services.GetRequiredService<IShellBackdropService>() : null;
            Model.PropertyChanged += ModelChanged;
            MotionPreferences.Changed += MotionChanged;
            UiThemeService.PaletteChanged += AmbientChanged;
            UpdateHeroSize(); UpdateBackdrop(); UpdateTabIndicator(false); RevealHero();
            _timer.Start();
            try { await Model.ActivateAsync(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
            { App.Services.GetRequiredService<ILoggingService>().Add(Models.LogLevel.Warning, "Library", "Some game details could not be loaded."); }
        };
        Unloaded += async (_, _) => { _installationSheet?.DismissInstallation(); _timer.Stop(); MotionPreferences.Changed -= MotionChanged; UiThemeService.PaletteChanged -= AmbientChanged; ResetMotion(); if (_owner is not null) _owner.SizeChanged -= OwnerSizeChanged; _owner = null; Model.PropertyChanged -= ModelChanged; await Model.DeactivateAsync(); };
        SizeChanged += (_, _) => UpdateHeroSize();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (IsInstallationOpen) _installationSheet?.CloseOverlay(); else Model.BackCommand.Execute(null); e.Handled = true; } };
        _timer.Tick += async (_, _) => await Model.RefreshPlayTimeAsync();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameDetailsViewModel.HeroArtwork)) { UpdateBackdrop(); RevealHero(); }
        if (e.PropertyName == nameof(GameDetailsViewModel.SelectedTab)) UpdateTabIndicator(true);
    }
    private void UpdateHeroSize()
    {
        GameHero.Height = Math.Clamp((Window.GetWindow(this)?.ActualHeight ?? ActualHeight) * .48, 300, 420);
        GameTitle.FontSize = Math.Clamp(ActualWidth * .03, 28, 38);
    }
    private void OwnerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeroSize();
    private void UpdateBackdrop()
    {
        _backdrop?.SetArtwork(Model.HeroArtwork);
        UpdateAmbient();
    }
    private void AmbientChanged(object? sender, EventArgs e) => UpdateAmbient();
    private void UpdateAmbient()
    {
        var light = !UiThemeService.AmbientEnabled && UiThemeService.StaticBackgroundIsLight;
        Resources["TextPrimaryBrush"] = light ? Brushes.Black : Brushes.White;
        Resources["TextSecondaryBrush"] = new SolidColorBrush(light ? Color.FromRgb(55, 59, 65) : Color.FromRgb(190, 195, 202));
        Resources["TextTertiaryBrush"] = new SolidColorBrush(light ? Color.FromRgb(85, 91, 99) : Color.FromRgb(130, 139, 151));
        Resources["DialogSurfaceBrush"] = new SolidColorBrush(Color.FromRgb(20, 22, 26));
        if (UiThemeService.IsHighContrastActive)
        {
            DetailsScene.Background = SystemColors.WindowBrush;
            GameDetailsWarmGradient.Background = Brushes.Transparent;
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush" }) Resources[key] = SystemColors.WindowTextBrush;
            Resources["DialogSurfaceBrush"] = SystemColors.WindowBrush;
            return;
        }
        if (!UiThemeService.AmbientEnabled)
        { DetailsScene.Background = UiThemeService.BackgroundBrush; GameDetailsWarmGradient.Background = Brushes.Transparent; return; }
        var background = Color.FromRgb(9, 11, 14);
        DetailsScene.Background = new SolidColorBrush(background);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(background), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        if (Model.HeroArtwork is { } artwork)
        {
            // Blur the actual banner once, keeping its surrounding colors and composition.
            // The live content stays sharp; scrolling never re-renders this bitmap.
            var visual = new DrawingVisual
            {
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 28, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality }
            };
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, new Rect(-48, -48, 416, 276));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            group.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, 1, 1)));
        }
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(100, background.R, background.G, background.B), 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(130, background.R, background.G, background.B), .35));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(195, background.R, background.G, background.B), .7));
        fade.GradientStops.Add(new GradientStop(background, 1));
        group.Children.Add(new GeometryDrawing(fade, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        var ambient = new DrawingBrush(group) { Stretch = Stretch.Fill };
        ambient.Freeze(); GameDetailsWarmGradient.Background = ambient;

    }
    private void TabRail_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabIndicator(false);
    private void UpdateTabIndicator(bool animate)
    {
        if (DataContext is not GameDetailsViewModel model || TabIndicator is null) return;
        TabIndicator.Width = TabRail.ActualWidth / 4;
        var target = model.SelectedTab * TabIndicator.Width;
        var from = TabSlide.X;
        TabSlide.X = target;
        TabSlide.BeginAnimation(TranslateTransform.XProperty, animate && MotionPreferences.AnimationsEnabled
            ? new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop } : null);
    }
    private void RevealHero()
    {
        if (!IsLoaded || !MotionPreferences.AnimationsEnabled) return;
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            HeroZoom.BeginAnimation(property, new DoubleAnimation(1.035, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private void DetailsScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DetailsScroll))
            HeroParallax.Y = MotionPreferences.AnimationsEnabled ? Math.Min(DetailsScroll.VerticalOffset * .16, GameHero.Height * .16) : 0;
    }
    private void MotionChanged(object? sender, EventArgs e) { if (!MotionPreferences.AnimationsEnabled) ResetMotion(); }
    private void ResetMotion()
    {
        HeroZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HeroZoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HeroParallax.Y = 0; UpdateTabIndicator(false);
    }
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Uri uri } || uri.Scheme != "https") return;
        try { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Model.HasDownload) { Model.OpenDownloads(); return; }
        if (!Model.CanInstall || Model.CatalogGame is not { } item) return;
        if (_installationSheet is null)
        {
            _installationSheet = new LibraryPage(installationOnly: true);
            _installationSheet.InstallationClosed += (_, _) =>
            {
                InstallationHost.Visibility = Visibility.Collapsed;
                DetailsScroll.Effect = null;
                InstallButton.Focus();
            };
            InstallationHost.Children.Add(new Frame
            {
                Content = _installationSheet,
                NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden,
                JournalOwnership = System.Windows.Navigation.JournalOwnership.OwnsJournal,
                Background = Brushes.Transparent
            });
        }
        InstallationHost.Visibility = Visibility.Visible;
        InstallationHost.UpdateLayout();
        if (MotionPreferences.BackdropBlurEnabled) DetailsScroll.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 7, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        _installationSheet.OpenDownloadSetup(item);
    }
    private async void ConnectSteam_Click(object sender, RoutedEventArgs e)
    {
        var key = SteamApiKey.Password; SteamApiKey.Clear();
        await Model.ConnectAchievementsAsync(key);
    }
    private void GetSteamKey_Click(object sender, RoutedEventArgs e)
    {
        try { using var process = Process.Start(new ProcessStartInfo("https://steamcommunity.com/dev/apikey") { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
    private void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the game's executable", Filter = "Game executable (*.exe)|*.exe", CheckFileExists = true };
        if (System.IO.Directory.Exists(Model.Game?.InstallFolder)) dialog.InitialDirectory = Model.Game!.InstallFolder;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Model.SetLaunchExecutable(dialog.FileName);
    }
    private void ResetExecutable_Click(object sender, RoutedEventArgs e) => Model.SetLaunchExecutable("");
}
