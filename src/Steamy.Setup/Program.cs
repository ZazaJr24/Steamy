using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Steamy.Setup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error)
            {
                if (args.Length >= 2 && args[0].StartsWith("--", StringComparison.Ordinal)) File.WriteAllText(Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar) + ".error.txt", error.ToString());
                else MessageBox.Show(error.Message, "Steamy Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        static int Run(string[] args)
        {
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            if (args.Length == 2 && args[0] == "--check-release")
            {
                try { using (var core = new InstallerCore()) { var package = core.GetPackage(CancellationToken.None).GetAwaiter().GetResult(); File.WriteAllText(args[1], new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(package)); } return 0; }
                catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--self-test") return InstallerChecks.Run(args[1]);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Length == 2 && args[0] == "--ui-test") return SetupWindow.CheckInterface(args[1]);
            bool preview = args.Length >= 2 && args.Length <= 4 && (args[0] == "--preview" || args[0] == "--preview-welcome" || args[0] == "--preview-options" || args[0] == "--preview-error" || args[0] == "--preview-ready" || args[0] == "--preview-motion");
            string fonts = preview ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "setup-fonts") : Path.Combine(InstallerCore.WorkRoot(), "ui-fonts");
            using (var window = new SetupWindow(preview, fonts))
            {
                if (preview)
                {
                    if (args[0] == "--preview-motion")
                    {
                        Directory.CreateDirectory(args[1]); window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false; window.Show(); window.UpdateLayout(); SetupWindow.Pump();
                        for (int i = 0; i < 180; i++) { window.Preview("--preview", i / 30d); window.Export(Path.Combine(args[1], "frame-" + i.ToString("000") + ".png"), args.Length >= 3 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 1); }
                        window.Close(); return 0;
                    }
                    window.Preview(args[0], args.Length >= 3 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0);
                    window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false; window.Show();
                    window.UpdateLayout(); SetupWindow.Pump();
                    window.Export(args[1], args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 1);
                    window.Close(); return 0;
                }
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                return app.Run(window);
            }
        }
    }
    static class SetupFonts
    {
        public static FontFamily Load(string folder)
        {
            try
            {
                InstallerCore.RejectLinks(folder); Directory.CreateDirectory(folder);
                foreach (var face in new[] { "Regular", "SemiBold" })
                {
                    string path = Path.Combine(folder, "GoogleSans-" + face + ".ttf");
                    if (File.Exists(path) && new FileInfo(path).Length > 2000000) continue;
                    using (var resource = typeof(SetupFonts).Assembly.GetManifestResourceStream("Steamy.Font." + face))
                    using (var zip = new DeflateStream(resource, CompressionMode.Decompress))
                    using (var output = File.Create(path)) zip.CopyTo(output);
                }
                string license = Path.Combine(folder, "OFL.txt");
                if (!File.Exists(license)) using (var resource = typeof(SetupFonts).Assembly.GetManifestResourceStream("Steamy.Font.License")) using (var output = File.Create(license)) resource.CopyTo(output);
                return new FontFamily(new Uri(Path.GetFullPath(folder) + Path.DirectorySeparatorChar), "./#Google Sans");
            }
            catch (IOException) { return new FontFamily("Segoe UI"); }
            catch (UnauthorizedAccessException) { return new FontFamily("Segoe UI"); }
        }
    }
    sealed class SetupWindow : Window, IDisposable
    {
        readonly InstallerCore installer = new InstallerCore();
        readonly FluidBackdrop backdrop = new FluidBackdrop();
        readonly Stopwatch clock = new Stopwatch();
        readonly Grid root;
        readonly bool displayOnly;
        CancellationTokenSource stop;
        bool busy, done, previewDownload, requestedClose, german, rendering;
        double percent, previewPhase, lastFrame;
        TimeSpan lastPresentation = TimeSpan.MinValue;
        int stage;
        string amount = "", error = "", detail = "";
        string installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Steamy");
        T View<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        string Choose(string english, string deutsch) { return german ? deutsch : english; }
        bool Motion { get { return !displayOnly && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast; } }
        bool Progress { get { return busy || done || previewDownload; } }
        public SetupWindow(bool preview, string fonts)
        {
            displayOnly = preview;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
            UseLayoutRounding = SnapsToDevicePixels = true;
            Width = 720; Height = 560; ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Title = "Steamy " + SetupBuild.Version + " Setup";
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#101016"));
            // Opaque WPF window: native DPI and hardware composition, without layered-window bitmap scaling.
            using (var stream = GetType().Assembly.GetManifestResourceStream("Steamy.SetupWindow.xaml")) root = (Grid)XamlReader.Load(stream);
            root.Resources["SetupFontFamily"] = SetupFonts.Load(fonts); Content = root;
            using (var stream = GetType().Assembly.GetManifestResourceStream("Steamy.Brand.xaml"))
            {
                var brand = (ResourceDictionary)XamlReader.Load(stream);
                View<Image>("HeaderLogo").Source = View<Image>("HeroLogo").Source = (DrawingImage)brand["SteamyBrandImage"];
            }
            View<Grid>("BackdropHost").Children.Add(backdrop);
            View<Button>("Primary").Click += delegate { StartOrLaunch(); };
            View<Button>("OptionsButton").Click += delegate { View<Border>("OptionsPanel").Visibility = View<Border>("OptionsPanel").Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateUi(); };
            View<Button>("FolderButton").Click += delegate { BrowseFolder(); };
            View<Button>("Cancel").Click += delegate { CancelDownload(); };
            View<Button>("MinimizeButton").Click += delegate { WindowState = WindowState.Minimized; };
            View<Button>("CloseButton").Click += delegate { Close(); };
            View<Button>("LanguageButton").Click += delegate { View<Border>("Languages").Visibility = View<Border>("Languages").Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            View<Button>("English").Click += delegate { german = false; View<Border>("Languages").Visibility = Visibility.Collapsed; UpdateUi(); };
            View<Button>("German").Click += delegate { german = true; View<Border>("Languages").Visibility = Visibility.Collapsed; UpdateUi(); };
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            { View<Border>("Languages").Visibility = Visibility.Collapsed; if (e.GetPosition(root).Y < 64) DragMove(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                if (View<Border>("Languages").Visibility == Visibility.Visible) View<Border>("Languages").Visibility = Visibility.Collapsed;
                else if (View<Border>("OptionsPanel").Visibility == Visibility.Visible) { View<Border>("OptionsPanel").Visibility = Visibility.Collapsed; UpdateUi(); }
                else if (busy) CancelDownload(); else Close(); e.Handled = true;
            };
            Loaded += delegate { UpdateUi(); RefreshMotion(); if (!displayOnly) root.Focus(); };
            StateChanged += delegate { RefreshMotion(); };
            SourceInitialized += delegate
            {
                try { int corner = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref corner, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            };
            SystemParameters.StaticPropertyChanged += PreferencesChanged;
            Closing += delegate(object sender, CancelEventArgs e)
            { if (busy) { e.Cancel = true; requestedClose = true; CancelDownload(); } };
            Closed += delegate { StopMotion(); SystemParameters.StaticPropertyChanged -= PreferencesChanged; };
            UpdateUi();
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        void PreferencesChanged(object sender, PropertyChangedEventArgs e) { Dispatcher.BeginInvoke((Action)RefreshMotion); }
        void RefreshMotion()
        {
            backdrop.HighContrast = SystemParameters.HighContrast;
            backdrop.Effect = !SystemParameters.HighContrast && (RenderCapability.Tier >> 16) >= 2 ? new System.Windows.Media.Effects.BlurEffect { Radius = 28, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality } : null;
            View<Button>("Primary").Tag = Motion;
            if (Motion && IsLoaded && WindowState != WindowState.Minimized)
            {
                if (!rendering) { lastFrame = -1; lastPresentation = TimeSpan.MinValue; clock.Start(); CompositionTarget.Rendering += RenderFrame; rendering = true; }
            }
            else { StopMotion(); backdrop.InvalidateVisual(); }
        }
        void StopMotion() { if (rendering) CompositionTarget.Rendering -= RenderFrame; rendering = false; clock.Stop(); }
        void RenderFrame(object sender, EventArgs e)
        {
            double seconds = clock.Elapsed.TotalSeconds;
            var composition = (RenderingEventArgs)e;
            if (composition.RenderingTime == lastPresentation) return;
            lastPresentation = composition.RenderingTime;
            // Hardware follows every display frame. A stopwatch cutoff at exactly
            // 1/60 would skip alternating frames on monitors whose ticks vary slightly.
            if ((RenderCapability.Tier >> 16) < 2 && seconds - lastFrame < 1d / 30 - .001) return;
            lastFrame = seconds; ApplyPhase(seconds);
        }
        void ApplyPhase(double seconds)
        {
            backdrop.Phase = seconds * .55; backdrop.InvalidateVisual();
            var lift = (TranslateTransform)View<Image>("HeroLogo").RenderTransform;
            lift.Y = Math.Sin(seconds * .9) * 2;
            View<Border>("ProgressHighlight").Margin = new Thickness(Math.Max(0, (View<Grid>("DownloadPanel").ActualWidth * percent / 100 - 40) * (.5 + .5 * Math.Sin(seconds * 2))), 0, 0, 0);
        }
        public void Preview(string mode, double seconds)
        {
            previewPhase = seconds; previewDownload = mode == "--preview"; done = mode == "--preview-ready";
            percent = done ? 100 : previewDownload ? 39 : 0; amount = "19.2 MB / 49.2 MB"; stage = 2;
            error = mode == "--preview-error" ? "The download could not be completed. Check your connection and try again." : "";
            View<Border>("OptionsPanel").Visibility = mode == "--preview-options" ? Visibility.Visible : Visibility.Collapsed;
            UpdateUi(); ApplyPhase(seconds);
        }
        void UpdateUi()
        {
            bool options = View<Border>("OptionsPanel").Visibility == Visibility.Visible && !Progress;
            View<Border>("OptionsPanel").Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            View<TextBlock>("Heading").Text = done ? Choose("Your library is ready.", "Deine Bibliothek ist bereit.") : error.Length > 0 ? Choose("Let’s try that again.", "Versuchen wir es erneut.") : Progress ? stage == 2 ? Choose("Downloading Steamy.", "Steamy wird heruntergeladen.") : stage == 3 ? Choose("Making everything ready.", "Alles wird eingerichtet.") : Choose("Preparing your library.", "Wir bereiten deine Bibliothek vor.") : Choose("One home for your games.", "Ein Zuhause für deine Spiele.");
            View<TextBlock>("Description").Text = options ? "" : error.Length > 0 ? error : done ? Choose("Steamy is installed. Your next game is waiting.", "Steamy ist installiert. Dein nächstes Spiel wartet.") : Progress ? stage == 2 ? Choose("The app and its components are on their way.\nWe’ll take care of the rest.", "Die App und ihre Komponenten sind unterwegs.\nWir kümmern uns um den Rest.") : stage == 3 ? Choose("Putting the last pieces in place.\nYour games and settings stay yours.", "Wir richten die letzten Komponenten ein.\nDeine Spiele und Einstellungen bleiben erhalten.") : Choose("Checking the components your library needs.\nEverything in one place, ready to play.", "Wir prüfen die benötigten Komponenten.\nAlles an einem Ort, bereit zum Spielen.") : Choose("We’ll get Steamy and its components ready.\nYou bring the games.", "Wir richten Steamy und seine Komponenten ein.\nDu bringst die Spiele mit.");
            View<TextBlock>("Eyebrow").Text = done ? Choose("READY WHEN YOU ARE", "BEREIT, WENN DU ES BIST") : Choose("YOUR LIBRARY, TOGETHER", "DEINE SPIELE, ZUSAMMEN");
            View<Button>("LanguageButton").Content = german ? "DE ﹀" : "EN ﹀";
            View<Button>("OptionsButton").Content = options ? Choose("Back to setup", "Zurück zur Installation") : Choose("Installation options", "Installationsoptionen");
            View<Button>("OptionsButton").Visibility = Progress ? Visibility.Collapsed : Visibility.Visible;
            View<Button>("OptionsButton").Margin = new Thickness(0, options ? 509 : 454, 0, 0);
            View<Button>("Primary").Visibility = !Progress || done ? Visibility.Visible : Visibility.Collapsed;
            View<Button>("Primary").Margin = new Thickness(0, done ? 468 : options ? 457 : 384, 0, 0);
            View<Button>("Primary").Content = done ? Choose("Open Steamy  →", "Steamy öffnen  →") : error.Length > 0 ? Choose("Try again  →", "Erneut versuchen  →") : Choose("Install Steamy  →", "Steamy installieren  →");
            View<Button>("Cancel").Content = Choose("Cancel download", "Download abbrechen");
            View<Button>("Cancel").Visibility = Progress && !done ? Visibility.Visible : Visibility.Collapsed;
            View<Button>("Cancel").IsEnabled = displayOnly || !installer.Finishing && stop != null && !stop.IsCancellationRequested;
            View<Grid>("DownloadPanel").Visibility = Progress ? Visibility.Visible : Visibility.Collapsed;
            View<TextBlock>("Amount").Text = amount; View<TextBlock>("Percentage").Text = ((int)percent).ToString(CultureInfo.CurrentCulture) + "%";
            View<TextBlock>("Detail").Text = busy && stage != 2 ? detail : done ? Choose("Installed in your selected folder", "In deinem gewählten Ordner installiert") : Choose("App + required components", "App + benötigte Komponenten");
            View<TextBlock>("FolderLabel").Text = Choose("INSTALL LOCATION", "INSTALLATIONSORDNER");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
            View<TextBlock>("Folder").Text = installFolder.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + installFolder.Substring(local.Length) : installFolder;
            View<TextBlock>("Folder").ToolTip = installFolder;
            View<Button>("FolderButton").Content = Choose("Change", "Ändern");
            View<CheckBox>("Shortcut").Content = Choose("Create a desktop shortcut", "Desktop-Verknüpfung erstellen");
            double width = Math.Max(0, (View<Grid>("DownloadPanel").ActualWidth > 0 ? View<Grid>("DownloadPanel").ActualWidth : 532) * percent / 100);
            var fill = View<Border>("ProgressFill");
            fill.BeginAnimation(WidthProperty, null);
            if (Motion && busy) fill.BeginAnimation(WidthProperty, new DoubleAnimation(fill.ActualWidth, width, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            fill.Width = width;
            View<Border>("ProgressHighlight").Visibility = percent > 8 && !done ? Visibility.Visible : Visibility.Collapsed;
            if (displayOnly) ApplyPhase(previewPhase);
        }
        void BrowseFolder()
        {
            using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = Choose("Choose an empty folder for Steamy", "Wähle einen leeren Ordner für Steamy"), SelectedPath = installFolder, ShowNewFolderButton = true })
            {
                if (picker.ShowDialog(new PickerOwner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK) { installFolder = picker.SelectedPath; UpdateUi(); }
            }
        }
        sealed class PickerOwner : System.Windows.Forms.IWin32Window { public IntPtr Handle { get; private set; } public PickerOwner(IntPtr handle) { Handle = handle; } }
        void CancelDownload()
        {
            if (displayOnly) { Close(); return; }
            if (stop == null || installer.Finishing) return;
            stop.Cancel(); detail = Choose("Cancelling…", "Wird abgebrochen…"); UpdateUi();
        }
        async void StartOrLaunch()
        {
            if (displayOnly) return;
            if (done)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(installFolder, "Steamy.exe")) { UseShellExecute = true, WorkingDirectory = installFolder }); Close(); }
                catch (Exception ex) { done = false; error = ex.Message; UpdateUi(); } return;
            }
            if (busy) return;
            try { InstallerCore.ValidateTarget(installFolder); }
            catch (Exception ex) { error = ex.Message; UpdateUi(); return; }
            busy = true; View<Border>("Languages").Visibility = Visibility.Collapsed; error = amount = ""; percent = 0; stage = 0;
            stop = new CancellationTokenSource(); UpdateUi(); View<Button>("Cancel").Focus();
            var progress = new Progress<InstallProgress>(value =>
            {
                stage = value.Percent < 4 ? 0 : value.Percent < 30 ? 1 : value.Percent < 86 ? 2 : 3; detail = value.Detail; percent = value.Percent;
                amount = value.Total > 0 ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB / {1:0.0} MB", value.Bytes / 1048576d, value.Total / 1048576d) : ""; UpdateUi();
            });
            try { await installer.Install(installFolder, View<CheckBox>("Shortcut").IsChecked == true, progress, stop.Token); done = true; percent = 100; amount = ""; }
            catch (OperationCanceledException) { error = Choose("Download cancelled. You can start again when you’re ready.", "Download abgebrochen. Du kannst erneut starten, wenn du bereit bist."); }
            catch (Exception ex) { error = ex.Message; }
            finally { busy = false; stop.Dispose(); stop = null; if (requestedClose) Close(); else { UpdateUi(); View<Button>("Primary").Focus(); } }
        }
        public void Export(string path, double scale)
        {
            if (scale < 1 || scale > 4) throw new ArgumentOutOfRangeException("scale");
            UpdateLayout(); ApplyPhase(previewPhase);
            // PNG/GIF has no physical display subpixels. Export ideal/grayscale glyphs
            // at the target resolution; keep ClearType/display formatting in the live UI.
            var text = new List<Tuple<TextBlock, object, object>>();
            PrepareExport(root, text);
            try
            {
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
            }
            finally
            {
                foreach (var value in text) { Restore(value.Item1, TextOptions.TextFormattingModeProperty, value.Item2); Restore(value.Item1, TextOptions.TextRenderingModeProperty, value.Item3); }
                root.UpdateLayout();
            }
        }
        static void Restore(DependencyObject item, DependencyProperty property, object value) { if (value == DependencyProperty.UnsetValue) item.ClearValue(property); else item.SetValue(property, value); }
        static void PrepareExport(DependencyObject item, List<Tuple<TextBlock, object, object>> text)
        {
            var label = item as TextBlock;
            if (label != null)
            {
                text.Add(Tuple.Create(label, label.ReadLocalValue(TextOptions.TextFormattingModeProperty), label.ReadLocalValue(TextOptions.TextRenderingModeProperty)));
                TextOptions.SetTextFormattingMode(label, TextFormattingMode.Ideal); TextOptions.SetTextRenderingMode(label, TextRenderingMode.Grayscale);
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) PrepareExport(VisualTreeHelper.GetChild(item, i), text);
        }
        public static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        static void Click(SetupWindow window, string name) { window.View<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.UpdateLayout(); Pump(); }
        public static int CheckInterface(string report)
        {
            try
            {
                using (var window = new SetupWindow(true, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "setup-fonts")))
                {
                    window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false; window.Preview("--preview-welcome", 0); window.Show(); window.UpdateLayout(); Pump();
                    if (!(window.View<Image>("HeroLogo").Source is DrawingImage)) throw new Exception("Logo must remain vector at every DPI.");
                    GlyphTypeface glyph;
                    if (!new Typeface(window.View<TextBlock>("Heading").FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal).TryGetGlyphTypeface(out glyph) || !glyph.FamilyNames.Values.Any(name => name.Contains("Google Sans"))) throw new Exception("Embedded brand fonts must load.");
                    if (window.AllowsTransparency || window.root.CacheMode != null) throw new Exception("Setup must use opaque native WPF composition.");
                    if (TextOptions.GetTextRenderingMode(window) != TextRenderingMode.ClearType || TextOptions.GetTextFormattingMode(window) != TextFormattingMode.Display) throw new Exception("ClearType/display formatting must be enabled.");
                    Click(window, "OptionsButton");
                    if (window.View<Border>("OptionsPanel").Visibility != Visibility.Visible || !window.View<Button>("FolderButton").IsVisible) throw new Exception("Folder options must open.");
                    window.View<CheckBox>("Shortcut").IsChecked = true;
                    if (window.View<CheckBox>("Shortcut").IsChecked != true) throw new Exception("Shortcut selection must persist.");
                    Click(window, "LanguageButton"); Click(window, "German");
                    if (!window.german || window.View<Border>("Languages").Visibility != Visibility.Collapsed || !window.View<Button>("Primary").Content.ToString().Contains("installieren")) throw new Exception("Language must update actions and close popup.");
                    var buttonPeer = UIElementAutomationPeer.CreatePeerForElement(window.View<Button>("Primary"));
                    if (buttonPeer == null || buttonPeer.GetPattern(PatternInterface.Invoke) == null || !window.View<Button>("Primary").Focusable) throw new Exception("Native WPF actions need keyboard and UI Automation support.");
                    var checkboxPeer = UIElementAutomationPeer.CreatePeerForElement(window.View<CheckBox>("Shortcut"));
                    if (checkboxPeer.GetPattern(PatternInterface.Toggle) == null) throw new Exception("Shortcut must expose the toggle pattern.");
                    Click(window, "OptionsButton");
                    if (window.View<Border>("OptionsPanel").Visibility != Visibility.Collapsed) throw new Exception("Options must close.");
                    Click(window, "Primary");
                    if (window.busy || window.stop != null) throw new Exception("Display-only primary action must never install.");
                    window.Preview("--preview", 2); window.UpdateLayout(); Pump();
                    if (!window.View<Button>("Cancel").IsVisible || window.View<Button>("Primary").IsVisible) throw new Exception("Download needs cancel without install button.");
                    if (Math.Abs(window.View<Border>("ProgressFill").Width - 532 * .39) > 1) throw new Exception("Progress width must match actual percent.");
                    if (window.busy || window.stop != null || window.rendering) throw new Exception("Previews must not download or start background motion.");
                    var first = window.backdrop.Phase; window.ApplyPhase(7);
                    if (first == window.backdrop.Phase) throw new Exception("The native fluid light must change with time.");
                    foreach (var scale in new[] { 1d, 1.5, 2d })
                    {
                        var png = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "dpi-" + (int)(scale * 100) + ".png"); window.Export(png, scale);
                        using (var stream = File.OpenRead(png)) { var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); if (frame.PixelWidth != (int)(720 * scale) || frame.PixelHeight != (int)(560 * scale)) throw new Exception("DPI render dimensions are incorrect."); }
                    }
                    window.Preview("--preview-ready", 0); window.UpdateLayout();
                    if (!window.View<Button>("Primary").IsVisible || window.View<Button>("Cancel").IsVisible || !window.View<Button>("Primary").Content.ToString().Contains("öffnen")) throw new Exception("Completion needs launch action.");
                    window.Preview("--preview-error", 0); window.UpdateLayout();
                    if (!window.View<TextBlock>("Description").Text.Contains("connection") || !window.View<Button>("Primary").IsVisible) throw new Exception("Errors need readable retry state.");
                    window.Close();
                }
                using (var live = new SetupWindow(false, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "setup-fonts")))
                {
                    live.Opacity = 0; live.ShowInTaskbar = false; live.ShowActivated = false; live.Show(); Pump();
                    if (live.Motion && !live.rendering) throw new Exception("Live motion must use the composition clock.");
                    live.WindowState = WindowState.Minimized; Pump();
                    if (live.rendering || live.clock.IsRunning) throw new Exception("Minimizing must release the composition callback.");
                    live.WindowState = WindowState.Normal; Pump();
                    if (live.Motion && !live.rendering) throw new Exception("Motion must resume after restore.");
                    live.Close(); if (live.rendering || live.clock.IsRunning || live.busy) throw new Exception("Closing must stop motion without starting an install.");
                }
                File.WriteAllText(report, "25 WPF installer interface checks passed: vector artwork and embedded fonts, opaque composition, ClearType, options, language, keyboard/UI Automation, preview isolation, progress, 100/150/200% rendering, ready/error states, live motion and minimize/restore/close cleanup. No real app or runtime installed."); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
        }
        public void Dispose() { StopMotion(); SystemParameters.StaticPropertyChanged -= PreferencesChanged; installer.Dispose(); }
    }
}

