using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;
using Steamy.Models;
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
    private ImageBrush? _chromeAmbient;
    private int _ambientAspectKey = -1;
    private ImageSource? _ambientSource;
    private bool _ambientPrepared;
    private CancellationTokenSource? _ambientRender;
    private CancellationTokenSource? _heroFadeRender;
    private ImageSource? _heroFadeSource;
    private int _heroFadeVariant = -1;
    private double _layoutWidth = double.NaN;
    private double _layoutDpi;
    private ImageSource? _layoutArtwork;
    internal ImageSource? AppliedAmbientArtwork { get; private set; }
    internal ImageSource? AppliedHeroArtwork { get; private set; }
    internal event EventHandler? ChromeSurfaceChanged;
    private IInputElement? _screenshotFocus;
    public LibraryPage? InstallationSheet => _installationSheet;
    public bool IsInstallationOpen => InstallationHost.Visibility == Visibility.Visible;
    public GameDetailsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<GameDetailsViewModel>();
        ScreenshotViewer.CloseRequested += (_, _) => CloseScreenshotViewer();
        Loaded += async (_, _) =>
        {
            CancelAmbient();
            _owner = Window.GetWindow(this);
            if (_owner is not null) _owner.SizeChanged += OwnerSizeChanged;
            _backdrop = Window.GetWindow(this) is MainWindow ? App.Services.GetRequiredService<IShellBackdropService>() : null;
            Model.PropertyChanged += ModelChanged;
            Model.AddToSteamRequested += OpenSteamSetup;
            MotionPreferences.Changed += MotionChanged;
            UiThemeService.PaletteChanged += AmbientChanged;
            _layoutWidth = double.NaN;
            UpdateHeroSize(); UpdateBackdrop(); UpdateTabIndicator(false);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => (_owner as MainWindow)?.RefreshCaptionAppearance()));
            _timer.Start();
            try { await Model.ActivateAsync(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
            { App.Services.GetRequiredService<ILoggingService>().Add(Models.LogLevel.Warning, "Library", "Some game details could not be loaded."); }
        };
        Unloaded += async (_, _) => { CancelAmbient(); CloseScreenshotViewer(); _installationSheet?.DismissInstallation(); _timer.Stop(); MotionPreferences.Changed -= MotionChanged; UiThemeService.PaletteChanged -= AmbientChanged; ResetMotion(); if (_owner is not null) _owner.SizeChanged -= OwnerSizeChanged; _owner = null; Model.PropertyChanged -= ModelChanged; Model.AddToSteamRequested -= OpenSteamSetup; await Model.DeactivateAsync(); };
        Unloaded += (_, _) => CancelHeroFade();
        SizeChanged += (_, _) => UpdateHeroSize();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (ScreenshotViewer.Visibility == Visibility.Visible) CloseScreenshotViewer(); else if (IsInstallationOpen) _installationSheet?.CloseOverlay(); else Model.BackCommand.Execute(null); e.Handled = true; } };
        _timer.Tick += async (_, _) => await Model.RefreshPlayTimeAsync();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameDetailsViewModel.HeroArtwork)) { UpdateHeroSize(); UpdateBackdrop(); _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => (_owner as MainWindow)?.RefreshCaptionAppearance())); }
        if (e.PropertyName == nameof(GameDetailsViewModel.SelectedTab)) UpdateTabIndicator(true);
        if (e.PropertyName == nameof(GameDetailsViewModel.Game)) { CloseScreenshotViewer(); _installationSheet?.DismissInstallation(); DetailsScroll.ScrollToTop(); GameToolsDisclosure.IsExpanded = false; }
    }
    private void UpdateHeroSize()
    {
        var source = Model.HeroArtwork;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(ActualWidth - _layoutWidth) < .5 && dpi == _layoutDpi && ReferenceEquals(source, _layoutArtwork)) return;
        _layoutWidth = ActualWidth; _layoutDpi = dpi; _layoutArtwork = source;
        GameHero.Height = Math.Clamp(ActualWidth * .24, 250, 380);
        var contentWidth = Math.Max(0, Math.Min(1120, ActualWidth - 80));
        GameIdentityLayout.Width = Math.Min(880, contentWidth);
        HeroArtworkFade.Height = GameHero.Height + 260;
        // Size artwork independently from the long ambient continuation. Stretching
        // a wide Steam banner over both areas enlarged it and cut off the subject.
        var aspectRatio = source is null || source.Height <= 0 ? 3.1 : source.Width / source.Height;
        HeroScene.Height = Math.Clamp(ActualWidth / Math.Max(1, aspectRatio), GameHero.Height, GameHero.Height + 160);
        UpdateHeroFade();
        GameTitle.FontSize = Math.Clamp(ActualWidth * .026, 28, 36);
        DetailsBody.Width = contentWidth;
        Resources["DetailPreviewHeight"] = Math.Max(80, (DetailsBody.Width / 4 - 14) * .625);
        var aspect = Math.Max(1, ActualWidth) / GameHero.Height;
        if (IsLoaded && GameDetailsAmbient.AspectKey(aspect) != _ambientAspectKey) UpdateAmbient();
    }
    private void CancelHeroFade()
    {
        _heroFadeRender?.Cancel(); _heroFadeRender?.Dispose(); _heroFadeRender = null;
        _heroFadeSource = null; _heroFadeVariant = -1;
        AppliedHeroArtwork = null;
        HeroImage.BeginAnimation(OpacityProperty, null); HeroImage.Source = null;
    }
    private async void UpdateHeroFade()
    {
        var source = Model.HeroArtwork;
        if (source is null) { CancelHeroFade(); return; }
        var variant = GameHeroFade.Variant(ActualWidth * _layoutDpi, Math.Max(1, ActualWidth) / HeroScene.Height);
        if (ReferenceEquals(source, _heroFadeSource) && variant == _heroFadeVariant) return;
        if (!ReferenceEquals(source, _heroFadeSource)) HeroImage.Source = null;
        _heroFadeRender?.Cancel(); _heroFadeRender?.Dispose();
        var request = _heroFadeRender = new CancellationTokenSource();
        var token = request.Token;
        _heroFadeSource = source; _heroFadeVariant = variant;
        try
        {
            var image = await GameHeroFade.ForAsync(source, variant, token);
            if (!IsLoaded || token.IsCancellationRequested || _heroFadeRender != request) return;
            HeroImage.Source = image;
            AppliedHeroArtwork = source;
            RevealHero();
            ChromeSurfaceChanged?.Invoke(this, EventArgs.Empty);
            (_owner as MainWindow)?.QueueCaptionAppearance();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            _heroFadeVariant = -1;
            App.Services.GetRequiredService<ILoggingService>().Add(LogLevel.Warning, "Library", "The game banner could not be prepared.");
        }
    }
    private void OwnerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeroSize();
    private void UpdateBackdrop()
    {
        _backdrop?.SetArtwork(Model.HeroArtwork);
        UpdateAmbient();
    }
    private void AmbientChanged(object? sender, EventArgs e) => UpdateAmbient(force: true);
    private void CancelAmbient()
    {
        _ambientRender?.Cancel(); _ambientRender?.Dispose(); _ambientRender = null;
        _ambientPrepared = false; _ambientSource = null; _ambientAspectKey = -1;
        AppliedAmbientArtwork = null;
    }
    private async void UpdateAmbient(bool force = false)
    {
        var aspect = Math.Max(1, ActualWidth) / GameHero.Height;
        var aspectKey = GameDetailsAmbient.AspectKey(aspect);
        var artwork = Model.HeroArtwork;
        // Loaded, hero arrival and owner/page resizing can describe the same
        // backdrop. Keep that request and its brushes instead of cancelling it
        // and invalidating every detail card's dynamic resources again.
        if (!force && _ambientPrepared && ReferenceEquals(_ambientSource, artwork) && _ambientAspectKey == aspectKey) return;
        CancelAmbient();
        _ambientPrepared = true; _ambientSource = artwork; _ambientAspectKey = aspectKey;
        _chromeAmbient = null;
        var light = UiThemeService.AmbientEnabled ? UiThemeService.IsLight : UiThemeService.StaticBackgroundIsLight;
        Resources["TextPrimaryBrush"] = light ? Brushes.Black : Brushes.White;
        Resources["TextSecondaryBrush"] = new SolidColorBrush(light ? Color.FromRgb(55, 59, 65) : Color.FromRgb(225, 228, 233));
        Resources["TextTertiaryBrush"] = new SolidColorBrush(light ? Color.FromRgb(85, 91, 99) : Color.FromRgb(220, 225, 232));
        Resources["DetailSurfaceBrush"] = light ? Application.Current.Resources["SurfaceBrush"] : Resources["DetailGlassTintBrush"];
        Resources["DetailPrimaryActionBackground"] = light ? Application.Current.Resources["AccentBrush"] : Brushes.White;
        Resources["DetailPrimaryActionForeground"] = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(36, 53, 74));
        HeroTopShade.Visibility = light ? Visibility.Collapsed : Visibility.Visible;
        Resources["DialogSurfaceBrush"] = light ? Application.Current.Resources["DialogSurfaceBrush"]
            : new LinearGradientBrush(Color.FromArgb(244, 8, 8, 10), Color.FromArgb(236, 2, 2, 4), 90);
        if (UiThemeService.IsHighContrastActive)
        {
            Resources["DetailEdgeBrush"] = SystemColors.WindowTextBrush;
            DetailsScene.Background = SystemColors.WindowBrush;
            DetailsChromeScrim.Background = SystemColors.WindowBrush;
            HeroArtworkFade.Visibility = Visibility.Collapsed;
            GameDetailsWarmGradient.Background = Brushes.Transparent;
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush" }) Resources[key] = SystemColors.WindowTextBrush;
            Resources["DialogSurfaceBrush"] = Resources["DetailSurfaceBrush"] = SystemColors.WindowBrush;
            Resources["DetailPrimaryActionBackground"] = SystemColors.HighlightBrush;
            Resources["DetailPrimaryActionForeground"] = SystemColors.HighlightTextBrush;
            return;
        }
        HeroArtworkFade.Visibility = Visibility.Visible;
        if (!UiThemeService.AmbientEnabled)
        { Resources["DetailEdgeBrush"] = new SolidColorBrush(light ? Color.FromArgb(36, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255)); DetailsScene.Background = UiThemeService.BackgroundBrush; DetailsChromeScrim.Background = UiThemeService.BackgroundBrush; GameDetailsWarmGradient.Background = Brushes.Transparent; return; }
        Resources["DetailEdgeBrush"] = new SolidColorBrush(light ? Color.FromArgb(45, 20, 35, 55) : Color.FromArgb(36, 255, 255, 255));
        DetailsScene.Background = light ? Brushes.White : Brushes.Black;
        if (artwork is not { } source)
        {
            GameDetailsWarmGradient.Background = Brushes.Transparent;
            DetailsChromeScrim.Background = DetailsScene.Background;
            return;
        }
        var request = _ambientRender = new CancellationTokenSource();
        var token = request.Token;
        BitmapSource image;
        try
        {
            if (light)
            {
                DetailsChromeScrim.Background = Brushes.White;
                GameDetailsWarmGradient.Background = Brushes.Transparent;
                var palette = await ArtworkPalette.FromImageAsync(source, token);
                if (_ambientRender != request || token.IsCancellationRequested || !IsLoaded) return;
                GameDetailsWarmGradient.Background = PaperAmbient.Create(palette);
                AppliedAmbientArtwork = source;
                UpdateChromeAmbient();
                (_owner as MainWindow)?.QueueCaptionAppearance();
                return;
            }
            image = await GameDetailsAmbient.ForAsync(source, aspect, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            if (_ambientRender == request) CancelAmbient();
            App.Services.GetRequiredService<ILoggingService>().Add(Models.LogLevel.Warning, "Library", "The artwork background could not be rendered.");
            return;
        }
        if (_ambientRender != request || token.IsCancellationRequested || !IsLoaded) return;
        var drawing = new ImageDrawing(image, new Rect(0, 0, 1, 1));
        var ambient = new DrawingBrush(drawing) { Stretch = Stretch.Fill }; ambient.Freeze();
        GameDetailsWarmGradient.Background = ambient;
        _chromeAmbient = new ImageBrush(image) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
        DetailsChromeScrim.Background = _chromeAmbient;
        AppliedAmbientArtwork = source;
        UpdateChromeAmbient();
        (_owner as MainWindow)?.QueueCaptionAppearance();
    }
    private void UpdateChromeAmbient()
    {
        DetailsChromeScrim.Opacity = Math.Clamp(DetailsScroll.VerticalOffset / 120, 0, 1);
        if (_chromeAmbient is not null && DetailsScene.ActualHeight > 0)
        {
            var height = Math.Min(1, 64 / DetailsScene.ActualHeight);
            var top = Math.Clamp(DetailsScroll.VerticalOffset / DetailsScene.ActualHeight, 0, 1 - height);
            _chromeAmbient.Viewbox = new Rect(0, top, 1, height);
        }
        ChromeSurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    // Compose only the frozen background layers for the shell's narrow glass strip.
    // A VisualBrush of this entire page also renders every card, control and effect
    // again for each glass surface and for synchronous caption-contrast readback.
    internal void DrawChromeSurface(DrawingContext drawing, FrameworkElement coordinates, Rect target)
    {
        drawing.PushClip(new RectangleGeometry(target));
        var scene = new Rect(DetailsScene.TranslatePoint(new Point(), coordinates), DetailsScene.RenderSize);
        drawing.DrawRectangle(DetailsScene.Background, null, scene);
        drawing.DrawRectangle(GameDetailsWarmGradient.Background, null, scene);
        if (HeroArtworkFade.IsVisible && HeroImage.Source is { } image)
        {
            var banner = new Rect(HeroScene.TranslatePoint(new Point(), coordinates), HeroScene.RenderSize);
            drawing.PushOpacity(HeroImage.Opacity);
            drawing.DrawImage(image, banner);
            drawing.Pop();
        }
        if (HeroTopShade.IsVisible)
            drawing.DrawRectangle(HeroTopShade.Background, null,
                new Rect(HeroTopShade.TranslatePoint(new Point(), coordinates), HeroTopShade.RenderSize));
        if (DetailsChromeScrim.Opacity > 0 && DetailsChromeScrim.Background is { } scrim)
        {
            drawing.PushOpacity(DetailsChromeScrim.Opacity);
            drawing.PushOpacityMask(DetailsChromeScrim.OpacityMask);
            drawing.DrawRectangle(scrim, null, new Rect(DetailsChromeScrim.TranslatePoint(new Point(), coordinates), DetailsChromeScrim.RenderSize));
            drawing.Pop(); drawing.Pop();
        }
        drawing.Pop();
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
        // Keep the banner crop and text stationary. Only the arriving artwork fades;
        // moving the masked scene also repainted its full viewport during scrolling.
        HeroImage.BeginAnimation(OpacityProperty, IsLoaded && MotionPreferences.AnimationsEnabled
            ? new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop }
            : null);
    }
    private void DetailsScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DetailsScroll))
        {
            UpdateChromeAmbient();
            (_owner as MainWindow)?.QueueCaptionAppearance();
        }
    }
    private void MotionChanged(object? sender, EventArgs e) { if (!MotionPreferences.AnimationsEnabled) ResetMotion(); }
    private void ResetMotion()
    {
        HeroImage.BeginAnimation(OpacityProperty, null);
        HeroZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HeroZoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HeroParallax.Y = 0; UpdateTabIndicator(false);
    }
    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameScreenshot screenshot }) return;
        _screenshotFocus = Keyboard.FocusedElement;
        DetailsScroll.IsEnabled = false;
        ScreenshotViewer.Open(Model.Screenshots, Model.Screenshots.IndexOf(screenshot));
        e.Handled = true;
    }
    private void CloseScreenshotViewer()
    {
        ScreenshotViewer.Close(); DetailsScroll.IsEnabled = true;
        if (IsLoaded && _screenshotFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        _screenshotFocus = null;
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
        OpenInstallationSheet(item, addToSteam: false);
    }
    private void OpenSteamSetup(SteamCatalogItem item) => OpenInstallationSheet(item, addToSteam: true);
    private bool _addingToSteam;
    private void OpenInstallationSheet(SteamCatalogItem item, bool addToSteam)
    {
        _addingToSteam = addToSteam;
        if (_installationSheet is null)
        {
            _installationSheet = new LibraryPage(installationOnly: true);
            _installationSheet.InstallationClosed += (_, _) =>
            {
                InstallationHost.Visibility = Visibility.Collapsed;
                DetailsScroll.Effect = null;
                (_addingToSteam ? AddToSteamButton : InstallButton).Focus();
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
        if (addToSteam) _installationSheet.OpenSteamSetup(item);
        else _installationSheet.OpenDownloadSetup(item);
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
