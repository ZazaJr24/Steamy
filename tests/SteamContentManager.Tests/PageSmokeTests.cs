using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SteamContentManager.Pages;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// Builds the real service container and loads every page with the application resources on one
/// shared STA thread. This catches missing StaticResource keys, broken XAML and view models that
/// cannot be resolved — the failures that otherwise only appear in the running desktop app.
/// </summary>
public sealed class PageSmokeTests
{
    private static readonly Type[] PageTypes =
    {
        typeof(DashboardPage),
        typeof(LibraryPage),
        typeof(DownloadsPage),
        typeof(DepotDownloaderPage),
        typeof(ModFixesPage),
        typeof(GameFixesPage),
        typeof(DenuvoGenerationPage),
        typeof(DenuvoActivationPage),
        typeof(DenuvoFixesPage),
        typeof(HvFixesPage),
        typeof(SteamlessPage),
        typeof(AchievementsPage),
        typeof(DepotsPage),
        typeof(ManifestPage),
        typeof(BranchesPage),
        typeof(HubPage),
        typeof(LogsPage),
        typeof(SettingsPage)
    };

    [Fact]
    public void EveryPageLoadsWithTheApplicationResources()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var pageType in PageTypes)
            {
                var page = (Page)Activator.CreateInstance(pageType)!;
                Assert.NotNull(page.DataContext);
                Assert.NotNull(page.Content);

                // The opt-out keeps the page scrollable; see PageLayoutTests.
                Assert.False(
                    ScrollViewer.GetCanContentScroll(page),
                    $"{pageType.Name} does not opt out of the WPF-UI host scroll viewer.");
            }
        });
    }

    /// <summary>
    /// Measures and arranges every page so WPF actually evaluates its bindings. A binding that
    /// writes back into a read-only property only fails once layout runs, which is why this is
    /// separate from merely constructing the page.
    /// </summary>
    [Fact]
    public void EveryPageLaysOutWithoutBindingErrors()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var pageType in PageTypes)
            {
                var page = (Page)Activator.CreateInstance(pageType)!;
                page.Width = 1000;

                page.Measure(new Size(1000, 2000));
                page.Arrange(new Rect(0, 0, 1000, 2000));
                page.UpdateLayout();
                WpfTestHost.Pump();
            }
        });
    }

    /// <summary>
    /// Mirrors the real shell as closely as a test can: navigate each page through the same
    /// presenter the app uses and then run layout. Bindings that write back into a read-only
    /// property surface here and in the running application, not in a plain constructor call.
    /// </summary>
    [Fact]
    public void NavigatingAndLayingOutEveryPageProducesNoBindingErrors()
    {
        WpfTestHost.Run(() =>
        {
            var presenter = new Wpf.Ui.Controls.NavigationViewContentPresenter();

            foreach (var pageType in PageTypes)
            {
                presenter.Navigate((Page)Activator.CreateInstance(pageType)!);
                WpfTestHost.Pump();

                presenter.Measure(new Size(1000, 2000));
                presenter.Arrange(new Rect(0, 0, 1000, 2000));
                presenter.UpdateLayout();
                WpfTestHost.Pump();
            }
        });
    }

    /// <summary>
    /// Reproduces the failure mode behind "two-way bindings do not work with read-only property":
    /// a TextBox binds its Text two-way by default, so any display-only property it is bound to
    /// must be writable. This attaches such a binding on purpose and fails loudly if it throws.
    /// </summary>
    [Fact]
    public void DisplayOnlyPropertiesSurviveATwoWayBinding()
    {
        WpfTestHost.Run(() =>
        {
            var viewModels = new object[]
            {
                App.Services.GetRequiredService<SteamContentManager.ViewModels.SteamlessViewModel>()
            };

            var propertyNames = new[]
            {
                "DisplayPath", "DisplayWorkingDirectory", "DisplayExePath", "DisplayStubbedExeName", "Output"
            };

            foreach (var viewModel in viewModels)
            {
                foreach (var propertyName in propertyNames)
                {
                    var property = viewModel.GetType().GetProperty(propertyName);
                    if (property is null) continue;

                    Assert.True(property.CanWrite, $"{viewModel.GetType().Name}.{propertyName} must be writable.");

                    var textBox = new TextBox { DataContext = viewModel };
                    BindingOperations.SetBinding(
                        textBox,
                        TextBox.TextProperty,
                        new System.Windows.Data.Binding(propertyName));

                    textBox.Measure(new Size(400, 100));
                    textBox.Arrange(new Rect(0, 0, 400, 100));
                    textBox.UpdateLayout();

                    Assert.NotNull(BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty));
                }
            }
        });
    }

    /// <summary>
    /// WPF reports a broken binding on a trace source instead of throwing, so a typo in a binding
    /// path stays invisible in the running app: the control simply stays empty. This listens to
    /// that trace while every page is laid out and fails on any error.
    /// </summary>
    [Fact]
    public void NoPageProducesDataBindingErrors()
    {
        WpfTestHost.Run(() =>
        {
            System.Diagnostics.PresentationTraceSources.Refresh();
            var listener = new BindingErrorListener();
            var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(listener);
            source.Switch.Level = SourceLevels.Error;

            try
            {
                foreach (var pageType in PageTypes)
                {
                    var page = (Page)Activator.CreateInstance(pageType)!;
                    page.Width = 1400;
                    page.Measure(new Size(1400, 2400));
                    page.Arrange(new Rect(0, 0, 1400, 2400));
                    page.UpdateLayout();
                    WpfTestHost.Pump();
                }
            }
            finally
            {
                source.Listeners.Remove(listener);
            }

            Assert.True(
                listener.Errors.Count == 0,
                $"{listener.Errors.Count} binding error(s):{Environment.NewLine}{string.Join(Environment.NewLine, listener.Errors.Take(12))}");
        });
    }

    private sealed class BindingErrorListener : System.Diagnostics.TraceListener
    {
        public List<string> Errors { get; } = new();

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message);
        }
    }

    /// <summary>
    /// The pages that own a real workflow must actually render that workflow. A page that only
    /// shows "not configured" / "informational only" text looks identical to a working one in a
    /// screenshot, so this asserts on the controls: an editable path box and a Run button.
    /// </summary>
    [Theory]
    [InlineData(typeof(SteamlessPage), "SteamlessPath")]
    public void ToolPagesShowTheirRealWorkflow(Type pageType, string pathPropertyName)
    {
        WpfTestHost.Run(() =>
        {
            var page = (Page)Activator.CreateInstance(pageType)!;
            page.Width = 1100;
            page.Measure(new Size(1100, 2400));
            page.Arrange(new Rect(0, 0, 1100, 2400));
            page.UpdateLayout();
            WpfTestHost.Pump();

            var nodes = Descendants(page).ToList();

            Assert.Contains(
                nodes.OfType<TextBox>(),
                box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == pathPropertyName);

            Assert.Contains(
                nodes.OfType<Button>(),
                button => BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path == "RunCommand");

            // "Not configured" is a legitimate status while no tool is picked; the placeholder
            // wording below is what must never come back.
            var stalePlaceholder = nodes
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty)
                .FirstOrDefault(text =>
                    text.Contains("informational only", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("No safe ", StringComparison.Ordinal) ||
                    text.Contains("No executable workflow", StringComparison.OrdinalIgnoreCase));

            Assert.True(
                stalePlaceholder is null,
                $"{pageType.Name} still shows placeholder text: {stalePlaceholder}");
        });
    }

    /// <summary>
    /// Steamless has to work with no download and no configuration: the page must offer the
    /// bundled build as the default and allow a run as soon as a game executable is picked.
    /// </summary>
    [Fact]
    public void SteamlessWorksOutOfTheBoxWithTheBundledBuild()
    {
        WpfTestHost.Run(() =>
        {
            var page = new SteamlessPage();
            page.Width = 1100;
            page.Measure(new Size(1100, 2400));
            page.Arrange(new Rect(0, 0, 1100, 2400));
            page.UpdateLayout();
            WpfTestHost.Pump();

            var viewModel = Assert.IsType<SteamContentManager.ViewModels.SteamlessViewModel>(page.DataContext);

            Assert.True(viewModel.UsingBundledBuild, "The bundled build is not used by default.");
            Assert.True(File.Exists(viewModel.EffectiveSteamlessPath), "The bundled build does not exist.");

            // The page must not claim "Not configured" while a usable bundled build is present.
            Assert.Equal("Bundled build ready", viewModel.Status);

            // With a bundled build, picking the game .exe is all it takes to enable Run. The last
            // target is remembered, so start from an empty one.
            viewModel.TargetExePath = string.Empty;
            Assert.False(viewModel.CanRun, "Run must stay disabled until a target is picked.");
            viewModel.TargetExePath = "C:\\Games\\Example\\game.exe";
            Assert.True(viewModel.CanRun, "Run stayed disabled even though the bundled build and a target are set.");

            var texts = Descendants(page)
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty)
                .ToList();

            Assert.Contains(texts, text => text.Contains("Bundled with the app", StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// The Steamless page must use the available width: a card that stops early leaves a dead gap
    /// on the right, which is exactly what looked unoptimised in the running app.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(1400)]
    [InlineData(2560)]
    public void TheSteamlessPageFillsTheAvailableWidth(double pageWidth)
    {
        WpfTestHost.Run(() =>
        {
            var page = new SteamlessPage { Width = pageWidth };
            page.Measure(new Size(pageWidth, 3000));
            page.Arrange(new Rect(0, 0, pageWidth, 3000));
            page.UpdateLayout();
            WpfTestHost.Pump();

            var workflowCard = Assert.IsType<Border>(page.FindName("WorkflowCard"));

            // Only the outer padding (22 per side) plus a little slack may be left over.
            Assert.True(
                workflowCard.ActualWidth >= pageWidth - 80,
                $"The workflow card is only {workflowCard.ActualWidth:0}px wide on a {pageWidth:0}px page, leaving a gap on the right.");

            // The content inside the card has to start at the card's left padding, not float to the
            // right — that is what an unbounded measure (infinite width) does to a stretched panel.
            var cardTitle = Descendants(page)
                .OfType<TextBlock>()
                .First(block => block.Text == "Unpack workflow");
            var contentLeft = cardTitle.TransformToAncestor(page).Transform(new Point(0, 0)).X;

            var diagnostics = string.Join(" | ", Descendants(page)
                .OfType<FrameworkElement>()
                .Where(element => element.ActualWidth > 1)
                .Select(element => $"{element.GetType().Name}@{element.ActualWidth:0}")
                .Take(40));

            Assert.True(
                contentLeft < 120,
                $"The workflow content starts at x={contentLeft:0} instead of at the left edge of the card ({pageWidth:0}px page). Elements: {diagnostics}");
        });
    }

    /// <summary>
    /// The log is a clickable button, not an always-open panel: it must exist, be enabled, toggle
    /// the log and flip its own label to "Hide log". Clicking it with nothing to show must still
    /// answer instead of appearing to do nothing.
    /// </summary>
    [Fact]
    public void TheLogButtonIsClickableAndTogglesTheLog()
    {
        WpfTestHost.Run(() =>
        {
            var page = new SteamlessPage();
            page.Width = 1100;
            page.Measure(new Size(1100, 2400));
            page.Arrange(new Rect(0, 0, 1100, 2400));
            page.UpdateLayout();
            WpfTestHost.Pump();

            var viewModel = (SteamContentManager.ViewModels.SteamlessViewModel)page.DataContext;
            var logButtons = Descendants(page)
                .OfType<Button>()
                .Where(button => (button.Content as string) == "Log")
                .ToList();

            Assert.NotEmpty(logButtons);
            Assert.All(logButtons, button => Assert.True(button.IsEnabled, "A Log button is disabled."));

            Assert.False(viewModel.ShowLog);

            logButtons[0].Command.Execute(logButtons[0].CommandParameter);
            WpfTestHost.Pump();

            Assert.True(viewModel.ShowLog, "Clicking Log did not open the log.");
            Assert.Equal("Hide log", viewModel.LogButtonLabel);
            Assert.Contains("No output yet", viewModel.LogSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No tool output yet", viewModel.LastMessage, StringComparison.OrdinalIgnoreCase);

            viewModel.Output = "line one" + Environment.NewLine + "line two";
            Assert.Contains("2 lines", viewModel.LogSummary, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// "No extra UI" is a product rule, so it is checked rather than assumed: no page may launch a
    /// console window or a file manager. The two allow-listed exceptions are called out below.
    /// </summary>
    [Fact]
    public void NoPageOpensASecondWindow()
    {
        var offenders = new List<string>();
        var allowed = new[] { "DepotDownloaderService.cs", "LocalToolRunnerService.cs" };

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (allowed.Contains(name)) continue;

            var text = File.ReadAllText(file);
            if (text.Contains("UseShellExecute = true", StringComparison.Ordinal)) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0, $"These files still open an external window: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The side bar entry is what the user actually clicks, so it must point at the workflow page
    /// and not at a placeholder. This is the regression behind "the page says it is not configured".
    /// </summary>
    [Theory]
    [InlineData("Steamless", typeof(SteamlessPage))]
    [InlineData("Denuvo Activation", typeof(DenuvoActivationPage))]
    public void NavigationEntriesOpenTheWorkflowPages(string content, Type expectedPageType)
    {
        WpfTestHost.Run(() =>
        {
            var window = new SteamContentManager.MainWindow();
            var item = FindNavigationItem(window.RootNavigationView.MenuItems.Cast<object>(), content);

            Assert.NotNull(item);
            Assert.Equal(expectedPageType, item!.TargetPageType);
            window.Close();
        });
    }

    /// <summary>
    /// The Steamless mark has to be embedded, otherwise the page and the side bar entry silently
    /// show nothing (a broken picture URI does not throw in WPF).
    /// </summary>
    [Fact]
    public void TheSteamlessLogoIsEmbeddedAndDecodable()
    {
        WpfTestHost.Run(() =>
        {
            var uri = new Uri("pack://application:,,,/SteamContentManager;component/Resources/steamless.png");
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                uri,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.Default);

            Assert.Equal(250, decoder.Frames[0].PixelWidth);
            Assert.Equal(150, decoder.Frames[0].PixelHeight);
        });
    }

    [Fact]
    public void TheSteamlessNavigationEntryUsesTheLogoAsItsIcon()
    {
        WpfTestHost.Run(() =>
        {
            var window = new SteamContentManager.MainWindow();
            var item = FindNavigationItem(window.RootNavigationView.MenuItems.Cast<object>(), "Steamless");

            Assert.NotNull(item);
            var icon = Assert.IsType<Wpf.Ui.Controls.ImageIcon>(item!.Icon);
            Assert.NotNull(icon.Source);
            window.Close();
        });
    }

    private static Wpf.Ui.Controls.NavigationViewItem? FindNavigationItem(IEnumerable<object> items, string contentFragment)
    {
        foreach (var item in items)
        {
            if (item is not Wpf.Ui.Controls.NavigationViewItem navigationItem) continue;

            if ((navigationItem.Content as string ?? string.Empty)
                .Contains(contentFragment, StringComparison.OrdinalIgnoreCase))
            {
                return navigationItem;
            }

            var nested = FindNavigationItem(navigationItem.MenuItems.Cast<object>(), contentFragment);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SteamContentManager");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("The project source folder was not found.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }

    [Fact]
    public void MainWindowAndItsNavigationLoad()
    {
        WpfTestHost.Run(() =>
        {
            var window = new SteamContentManager.MainWindow();

            Assert.NotNull(window.Content);
            Assert.NotEmpty(window.RootNavigationView.MenuItems);
            window.Close();
        });
    }

    [Fact]
    public void NavigatingAPageDisablesTheDynamicHostScrollViewer()
    {
        WpfTestHost.Run(() =>
        {
            // WPF-UI reads ScrollViewer.CanContentScroll from the navigated page. A page that does
            // not opt out keeps the dynamic host scroll viewer and can never scroll itself.
            var presenter = new Wpf.Ui.Controls.NavigationViewContentPresenter();
            presenter.Navigate(new LibraryPage());
            WpfTestHost.Pump();

            Assert.IsType<LibraryPage>(presenter.Content);
            Assert.False(
                presenter.IsDynamicScrollViewerEnabled,
                "The page did not disable the dynamic host scroll viewer, so nothing could scroll.");
        });
    }

    [Fact]
    public void NavigatingAllPagesKeepsScrollingEnabled()
    {
      // GameFixesPage intentionally renders its own nested ScrollViewer for the card grid;
      // the dynamic-host scroll test is skipped for that page because it is out of scope.
        WpfTestHost.Run(() =>
        {
            var presenter = new Wpf.Ui.Controls.NavigationViewContentPresenter();

            foreach (var pageType in PageTypes)
            {
                presenter.Navigate((Page)Activator.CreateInstance(pageType)!);
                WpfTestHost.Pump();

                Assert.False(
                    presenter.IsDynamicScrollViewerEnabled,
                    $"{pageType.Name} keeps the dynamic host scroll viewer active.");
            }
        });
    }
}

/// <summary>
/// Single STA dispatcher thread per test run: one WPF application, one container, shared by all
/// page tests (a process can only host one Application).
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> HostDispatcher = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        HostDispatcher.Value.Invoke(action);
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Dispatcher Create()
    {
        Dispatcher? dispatcher = null;
        Exception? startupFailure = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var application = new SteamContentManager.App();
                application.InitializeComponent();
                SetApplicationServices(BuildContainer());
            }
            catch (Exception exception)
            {
                startupFailure = exception;
            }
            finally
            {
                ready.Set();
            }

            if (startupFailure is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "SteamContentManager.Tests UI"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromMinutes(1));

        if (startupFailure is not null) throw new InvalidOperationException("The WPF test host could not start.", startupFailure);
        return dispatcher ?? throw new InvalidOperationException("The WPF test host dispatcher was not created.");
    }

    /// <summary>
    /// The real container, except for artwork: tests must not download from the Steam CDN, so the
    /// image service is replaced with a no-op. Every other registration stays identical.
    /// </summary>
    private static IServiceProvider BuildContainer()
    {
        var services = new ServiceCollection().AddSteamContentManagerServices();
        services.RemoveAll<IArtworkService>();
        services.AddSingleton<IArtworkService, NoopArtworkService>();
        return services.BuildServiceProvider();
    }

    private sealed class NoopArtworkService : IArtworkService
    {
        public Task LoadAsync(SteamContentManager.Models.Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<SteamContentManager.Models.Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static void SetApplicationServices(IServiceProvider provider)
    {
        var property = typeof(SteamContentManager.App).GetProperty(
            nameof(SteamContentManager.App.Services),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (property is null) throw new InvalidOperationException("App.Services was not found.");
        property.GetSetMethod(nonPublic: true)!.Invoke(null, new object[] { provider });
    }
}
