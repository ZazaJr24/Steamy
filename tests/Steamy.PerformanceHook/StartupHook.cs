using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Opt-in diagnostics for the actual portable Steamy.exe, never installed with the app.
internal static class StartupHook
{
    public static void Initialize()
    {
        var report = Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_REPORT");
        if (string.IsNullOrEmpty(report) || !string.Equals(Path.GetFileName(Environment.ProcessPath), "Steamy.exe", StringComparison.OrdinalIgnoreCase)) return;
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null); // Child hosts must run normally.
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_SOFTWARE_RENDERING") == "1")
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ControlledMedia.Initialize();
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_WINDOWED") == "1")
        {
            var normalized = false;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (normalized || sender is not Window window || window.GetType().FullName != "Steamy.MainWindow") return;
                normalized = true;
                // Match the presented startup viewport in both old and new builds,
                // including versions which maximize again on ContentRendered.
                window.GetType().GetField("_maximizeOnFirstRender", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, false);
                window.WindowState = WindowState.Normal; window.Width = 1440; window.Height = 900;
                ControlledMedia.WindowLoaded(window);
            }));
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 600; i++)
                {
                    if (Application.Current is { } app)
                    {
                        var ready = await app.Dispatcher.InvokeAsync(() => app.MainWindow is { IsLoaded: true, IsVisible: true });
                        if (ready) { await (await app.Dispatcher.InvokeAsync(() => Probe.RunAsync(app.MainWindow!, report))).ConfigureAwait(false); return; }
                    }
                    await Task.Delay(50);
                }
                throw new TimeoutException("The real application did not show its window.");
            }
            catch (Exception error) { await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { error = error.ToString(), controlledMediaDiagnostic = ControlledMedia.Diagnostic() })); }
        });
    }
}

public class OfflineRefreshProxy : DispatchProxy
{
    public object? Snapshot;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!; var type = method.ReturnType;
        if (method.Name == "get_Snapshot") return Snapshot;
        if (type == typeof(bool)) return method.Name == "get_IsAvailable";
        if (type == typeof(string)) return "Offline performance fixture";
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        { var value = type.GenericTypeArguments[0]; return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(value).Invoke(null, [value.IsValueType ? Activator.CreateInstance(value) : null]); }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

internal static class Probe
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(object owner, string name) => owner.GetType().GetProperty(name, Any)?.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetProperty(name, Any)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] values) => owner.GetType().GetMethod(name, Any)!.Invoke(owner, values);
    private static IEnumerable<T> Views<T>(DependencyObject parent) where T : DependencyObject
    {
        // The observer must not allocate an iterator for every visual on every
        // visibility poll. Keep the same depth-first order with one work stack.
        var pending = new Stack<DependencyObject>();
        for (var i = VisualTreeHelper.GetChildrenCount(parent) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(parent, i));
        while (pending.TryPop(out var child))
        {
            if (child is T value) yield return value;
            for (var i = VisualTreeHelper.GetChildrenCount(child) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(child, i));
        }
    }
    private sealed record MemorySample(long privateBytes, long workingSetBytes, long managedBytes, double cpuMs,
        List<ChildSample> helpers, long totalPrivateBytes, long totalWorkingSetBytes, double sampledAtMs,
        long gcCommittedBytes, long gcHeapSizeBytes, long gcFragmentedBytes);
    private static MemorySample Memory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var helpers = ChildProcesses(process.Id);
        var gc = GC.GetGCMemoryInfo();
        return new(process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(false), process.TotalProcessorTime.TotalMilliseconds,
            helpers, process.PrivateMemorySize64 + helpers.Sum(child => child.PrivateBytes), process.WorkingSet64 + helpers.Sum(child => child.WorkingSetBytes),
            (DateTime.Now - process.StartTime).TotalMilliseconds, gc.TotalCommittedBytes, gc.HeapSizeBytes, gc.FragmentedBytes);
    }
    private sealed record ChildSample(int Pid, string Name, long PrivateBytes, long WorkingSetBytes, double CpuMs);
    private sealed record ThreadCpu(int id, string name, double cpuMs);
    private sealed class WindowActivity : IDisposable
    {
        private readonly Window _window;
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _activeSince, _activeTicks;
        internal WindowActivity(Window window)
        {
            _window = window; _activeSince = window.IsActive ? _started : 0;
            window.Activated += Activated; window.Deactivated += Deactivated;
        }
        private void Activated(object? sender, EventArgs args) { if (_activeSince == 0) _activeSince = Stopwatch.GetTimestamp(); }
        private void Deactivated(object? sender, EventArgs args)
        { if (_activeSince != 0) { _activeTicks += Stopwatch.GetTimestamp() - _activeSince; _activeSince = 0; } }
        internal object Snapshot()
        {
            var end = Stopwatch.GetTimestamp(); var active = _activeTicks + (_activeSince == 0 ? 0 : end - _activeSince);
            return new { isActiveAtEnd = _window.IsActive, activeMs = active * 1000d / Stopwatch.Frequency,
                observedMs = (end - _started) * 1000d / Stopwatch.Frequency, activeFraction = active / (double)Math.Max(1, end - _started) };
        }
        public void Dispose() { _window.Activated -= Activated; _window.Deactivated -= Deactivated; }
    }
    private static object IdleViewState(Window window, object dashboard)
    {
        var featured = Get(dashboard, "FeaturedGame");
        var assembly = window.GetType().Assembly;
        var services = (IServiceProvider)assembly.GetType("Steamy.App")!.GetProperty("Services", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var library = services.GetService(assembly.GetType("Steamy.ViewModels.LibraryViewModel")!)!;
        var pipelineType = assembly.GetType("Steamy.Services.ImagePipeline");
        var pipeline = pipelineType?.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return new
        {
            window.IsActive, state = window.WindowState.ToString(), paused = Get(dashboard, "SpotlightPaused"),
            libraryMode = Get(library, "SelectedMode"), catalogItems = (Get(library, "CatalogItems") as ICollection)?.Count,
            featured = featured is null ? null : Get(Get(featured, "Game")!, "Name"),
            cacheBytes = pipeline is null ? null : Get(pipeline, "CachedBytes"),
            pendingThreadPoolItems = ThreadPool.PendingWorkItemCount,
            generation0 = GC.CollectionCount(0), generation1 = GC.CollectionCount(1), generation2 = GC.CollectionCount(2),
            playingTrailers = Views<FrameworkElement>(window).Where(view => view.GetType().Name == "TrailerPreview" && Get(view, "IsPlaying") is true)
                .Select(view => Get(view, "AppId")).ToArray()
        };
    }
    private static async Task<object?> MeasureSettingsConstructionAsync(Assembly assembly)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_SETTINGS_CONSTRUCTION") != "1") return null;
        var pageType = assembly.GetType("Steamy.Pages.SettingsPage")!;
        var samples = new List<object>();
        for (var index = 0; index < 10; index++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var began = Stopwatch.GetTimestamp();
            var page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            var elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            samples.Add(new { index, elapsedMs, allocatedBytes });
            await Task.Delay(30);
        }
        return new { scope = "Ten additional page constructors on the real UI dispatcher, after every comparable timing/memory point. No attached visual tree or layout; excludes deferred Loaded work. Allocations use the constructing thread only. Isolated diagnostics, not navigation latency or process-RAM proof.", samples };
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static List<ThreadCpu> ThreadTimes()
    {
        using var process = Process.GetCurrentProcess();
        var result = new List<ThreadCpu>();
        foreach (ProcessThread thread in process.Threads)
        {
            using (thread)
            {
                try
                {
                    var name = ""; var handle = OpenThread(0x0800, false, (uint)thread.Id);
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetThreadDescription(handle, out var pointer) == 0 && pointer != IntPtr.Zero)
                                try { name = Marshal.PtrToStringUni(pointer) ?? ""; } finally { LocalFree(pointer); }
                        }
                        finally { CloseHandle(handle); }
                    }
                    result.Add(new(thread.Id, name, thread.TotalProcessorTime.TotalMilliseconds));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }
    private static async Task<object?> ExtendedIdleAsync(Window window)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_EXTENDED_IDLE") != "1") return null;
        var uiThreadId = GetCurrentThreadId();
        var points = new List<object>();
        var assembly = window.GetType().Assembly;
        var services = (IServiceProvider)assembly.GetType("Steamy.App")!.GetProperty("Services", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var dashboard = services.GetService(assembly.GetType("Steamy.ViewModels.DashboardViewModel")!)!;
        var previousPause = Get(dashboard, "SpotlightPaused");
        foreach (var scenario in new[] { "Minimized", "Normal", "NormalSpotlightPaused", "NormalSpotlightResumed" })
        {
            window.WindowState = scenario == "Minimized" ? WindowState.Minimized : WindowState.Normal;
            if (scenario == "NormalSpotlightPaused") Set(dashboard, "SpotlightPaused", true);
            if (scenario == "NormalSpotlightResumed") Set(dashboard, "SpotlightPaused", previousPause);
            if (scenario != "Minimized") window.Activate();
            // First let short-lived decode/render work settle. No diagnostic GC,
            // process suspension, controller changes or services are stopped here.
            await Task.Delay(10000);
            var before = Memory(); var threads = ThreadTimes(); var allocated = GC.GetTotalAllocatedBytes();
            var viewBefore = IdleViewState(window, dashboard);
            using var activity = new WindowActivity(window);
            var started = Stopwatch.GetTimestamp();
            await Task.Delay(10000);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var after = Memory(); var afterThreads = ThreadTimes();
            points.Add(new
            {
                mode = scenario, elapsedMs, before, after, viewBefore, viewAfter = IdleViewState(window, dashboard), activity = activity.Snapshot(),
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                oneCorePercent = (after.cpuMs - before.cpuMs) / elapsedMs * 100,
                threads = afterThreads.Select(thread => new
                {
                    thread.id, thread.name, isUiThread = thread.id == uiThreadId,
                    cpuMs = thread.cpuMs - (threads.FirstOrDefault(previous => previous.id == thread.id)?.cpuMs ?? 0)
                }).OrderByDescending(thread => thread.cpuMs).ToArray()
            });
        }
        Set(dashboard, "SpotlightPaused", previousPause);
        return new { scope = "Separate optional long-idle diagnosis after the comparable stress and minimize points. Ten seconds settling plus ten seconds sampled per scenario; no forced GC. NormalSpotlightPaused uses the existing in-memory carousel pause control; subsequent-time effects are not separated by this ordered single-run diagnosis. Threads which exited before the final snapshot are absent.", points };
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, ParentPid; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr handle, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr handle, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static List<ChildSample> ChildProcesses(int root)
    {
        var result = new List<ChildSample>(); var parents = new Dictionary<int, int>();
        var handle = CreateToolhelp32Snapshot(2, 0); if (handle == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            if (Process32FirstW(handle, ref entry)) do { parents[(int)entry.Pid] = (int)entry.ParentPid; } while (Process32NextW(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        var descendants = new HashSet<int> { root };
        for (var added = true; added;)
        { added = false; foreach (var pair in parents) if (descendants.Contains(pair.Value) && descendants.Add(pair.Key)) added = true; }
        foreach (var pid in descendants.Where(pid => pid != root))
            try { using var child = Process.GetProcessById(pid); child.Refresh(); result.Add(new(pid, child.ProcessName, child.PrivateMemorySize64, child.WorkingSet64, child.TotalProcessorTime.TotalMilliseconds)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return result;
    }
    private static object HelperSummary(IEnumerable<MemorySample> samples)
    {
        var points = samples.ToArray();
        return new
        {
            scope = "Observed at the existing startup, cycle, retained and minimize sample points; short-lived children between samples may be missed. Peaks are observed sample maxima, not continuous peak counters.",
            observedHelpers = points.SelectMany(point => point.helpers).GroupBy(child => child.Pid).Select(group => new
            {
                pid = group.Key, name = group.First().Name, maxPrivateBytes = group.Max(child => child.PrivateBytes),
                maxWorkingSetBytes = group.Max(child => child.WorkingSetBytes), maxCpuMs = group.Max(child => child.CpuMs)
            }).ToArray(),
            maxHelperPrivateBytes = points.Max(point => point.helpers.Sum(child => child.PrivateBytes)),
            maxCombinedPrivateBytes = points.Max(point => point.totalPrivateBytes),
            maxCombinedWorkingSetBytes = points.Max(point => point.totalWorkingSetBytes)
        };
    }
    private static object RealizedPanels(DependencyObject root) => Views<Panel>(root)
        .Where(panel => panel is VirtualizingStackPanel || panel.GetType().Name == "VirtualizingGamePanel")
        .Where(panel => panel.IsVisible)
        .Select(panel => new { panel = panel.GetType().Name, realizedChildren = VisualTreeHelper.GetChildrenCount(panel), width = panel.ActualWidth, height = panel.ActualHeight })
        .ToArray();
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        internal int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Network is disabled for the cached-image measurement."));
        }
    }
    private static async Task<object> MeasureFreshDiskImagesAsync(Assembly assembly)
    {
        // A fresh loader per sample rules out its in-memory image cache. The OS file
        // cache is deliberately left alone. This measures read + decode completion,
        // separately from the legacy shared-service repeat-load measurements below.
        var profile = Environment.GetEnvironmentVariable("STEAMY_DATA_DIR")!;
        var cacheDirectory = Path.Combine(profile, "artwork");
        var file = Path.Combine(cacheDirectory, "1808500_hero.jpg");
        if (!File.Exists(file)) return new { available = false, reason = "The seeded local hero is missing." };
        var pipelineType = assembly.GetType("Steamy.Services.ImagePipeline");
        var requestType = assembly.GetType("Steamy.Services.ImageRequest");
        var usePipeline = pipelineType is not null && requestType is not null;
        var samples = new List<object>();
        for (var index = 0; index < 5; index++)
        {
            using var handler = new NoNetworkHandler();
            using var http = new HttpClient(handler);
            object loader = usePipeline
                ? Activator.CreateInstance(pipelineType!, [http, Path.Combine(profile, "image-cache-measurement"), 32L * 1024 * 1024, 512L * 1024 * 1024])!
                : Activator.CreateInstance(assembly.GetType("Steamy.Services.SteamArtworkService")!, [http, cacheDirectory])!;
            try
            {
                Task task;
                var began = Stopwatch.GetTimestamp();
                if (usePipeline)
                {
                    var request = Activator.CreateInstance(requestType!, [new Uri("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1808500/library_hero.jpg"), 1600, 0,
                        Enum.Parse(assembly.GetType("Steamy.Services.ImagePriority")!, "CacheOnly"),
                        Enum.Parse(assembly.GetType("Steamy.Services.ImageCachePolicy")!, "PublicArtwork"), file])!;
                    task = (Task)loader.GetType().GetMethod("LoadAsync")!.Invoke(loader, [request, CancellationToken.None])!;
                }
                else task = (Task)loader.GetType().GetMethod("LoadHeroAsync")!.Invoke(loader, [1808500, CancellationToken.None])!;
                await task;
                var elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                var image = task.GetType().GetProperty("Result")?.GetValue(task) as BitmapSource;
                samples.Add(new { elapsedMs, loaded = image is not null, pixelWidth = image?.PixelWidth, pixelHeight = image?.PixelHeight, frozen = image?.IsFrozen, networkAttempts = handler.Attempts });
            }
            finally { (loader as IDisposable)?.Dispose(); }
        }
        return new
        {
            available = true,
            method = usePipeline ? "Fresh ImagePipeline, public CacheOnly request, explicit seeded cache file, 1600 px target" : "Fresh SteamArtworkService per sample, seeded artwork directory, network denied",
            scope = "Local file read and decode task completion with no loader memory-cache hit; OS filesystem cache is not flushed. Does not include attaching the image or presenting a screen frame.",
            sourceFileBytes = new FileInfo(file).Length, sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))), samples
        };
    }
    private sealed class NetworkImageHandler : DelegatingHandler
    {
        private sealed record RequestSample(string uri, int? httpStatus, double headersMs, string? error);
        private readonly List<RequestSample> _requests = new();
        internal NetworkImageHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        internal object Snapshot() { lock (_requests) return _requests.ToArray(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var began = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.SendAsync(request, token).ConfigureAwait(false);
                lock (_requests) _requests.Add(new(request.RequestUri!.AbsoluteUri, (int)response.StatusCode, Stopwatch.GetElapsedTime(began).TotalMilliseconds, null));
                return response;
            }
            catch (Exception error)
            {
                lock (_requests) _requests.Add(new(request.RequestUri!.AbsoluteUri, null, Stopwatch.GetElapsedTime(began).TotalMilliseconds, error.GetType().Name));
                throw;
            }
        }
    }
    private static async Task<object> MeasureNetworkImageAsync(Assembly assembly)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_NETWORK_IMAGES") == "0")
            return new { attempted = false, reason = "Disabled by STEAMY_PERFORMANCE_NETWORK_IMAGES=0." };
        var profile = Environment.GetEnvironmentVariable("STEAMY_DATA_DIR")!;
        var directory = Path.Combine(profile, "network-image-measurement-" + Guid.NewGuid().ToString("N"));
        var uri = new Uri("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1808500/library_hero.jpg");
        var pipelineType = assembly.GetType("Steamy.Services.ImagePipeline");
        var requestType = assembly.GetType("Steamy.Services.ImageRequest");
        var usePipeline = pipelineType is not null && requestType is not null;
        using var handler = new NetworkImageHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy-Performance/0.8");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        object loader = usePipeline
            ? Activator.CreateInstance(pipelineType!, [http, directory, 32L * 1024 * 1024, 512L * 1024 * 1024])!
            : Activator.CreateInstance(assembly.GetType("Steamy.Services.SteamArtworkService")!, [http, directory])!;
        var began = Stopwatch.GetTimestamp(); BitmapSource? image = null; string? failure = null;
        try
        {
            Task task;
            if (usePipeline)
            {
                var request = Activator.CreateInstance(requestType!, [uri, 1600, 0,
                    Enum.Parse(assembly.GetType("Steamy.Services.ImagePriority")!, "Visible"),
                    Enum.Parse(assembly.GetType("Steamy.Services.ImageCachePolicy")!, "PublicArtwork"), null])!;
                task = (Task)loader.GetType().GetMethod("LoadAsync")!.Invoke(loader, [request, deadline.Token])!;
            }
            else task = (Task)loader.GetType().GetMethod("LoadHeroAsync")!.Invoke(loader, [1808500, deadline.Token])!;
            await task.WaitAsync(deadline.Token);
            image = task.GetType().GetProperty("Result")?.GetValue(task) as BitmapSource;
            if (image is null) failure = "The loader returned no image.";
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally { (loader as IDisposable)?.Dispose(); }
        var elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        var savedBytes = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
        return new
        {
            attempted = true, loaded = image is not null, elapsedMs, cancelledOrDeadlineReached = deadline.IsCancellationRequested, error = failure,
            pixelWidth = image?.PixelWidth, pixelHeight = image?.PixelHeight, frozen = image?.IsFrozen, savedBytes,
            method = usePipeline ? "Fresh ImagePipeline, public visible request, empty isolated disk/memory cache, 1600 px target" : "Fresh SteamArtworkService, empty isolated cache, public hero request (service fallbacks allowed)",
            scope = "Total network request, body download and decode completion; separate from local-file and repeated RAM-cache measurements. One optional request experiment after comparable memory/timing samples, eight-second total deadline, no account or credentials. HTTP header timings are not full body download timings.",
            requestedUri = uri.AbsoluteUri, requests = handler.Snapshot()
        };
    }
    internal static async Task RunAsync(Window window, string report)
    {
        var nativeMemory = new List<object>();
        void MapNative(string phase)
        {
            if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_MEMORY_MAP") != "1") return;
            nativeMemory.Add(new { phase, process = Memory(), map = NativeMemoryMap.Capture(),
                renderTree = Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_RENDER_TREE") == "1" ? RenderTreeDiagnostic.Capture(window) : null });
        }
        var assembly = window.GetType().Assembly;
        var services = (IServiceProvider)assembly.GetType("Steamy.App")!.GetProperty("Services", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        object Service(string name) => services.GetService(assembly.GetType(name)!)!;
        var firstVisibleMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var firstReadyMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var startupWindow = new { state = window.WindowState.ToString(), width = window.ActualWidth, height = window.ActualHeight };
        window.WindowState = WindowState.Normal; window.Width = 1440; window.Height = 900; window.UpdateLayout();
        await Task.Delay(1500); var idle = Memory();
        var dpi = VisualTreeHelper.GetDpi(window);
        var environment = new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            lightTheme = assembly.GetType("Steamy.Services.UiThemeService")?.GetProperty("IsLight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), renderTier = RenderCapability.Tier >> 16,
            renderPreference = RenderOptions.ProcessRenderMode.ToString(),
            windowWidth = window.ActualWidth, windowHeight = window.ActualHeight, dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY, startupWindow };
        // The real startup scan owns the shared game collection. Wait for its
        // completion before injecting fixtures so a late result cannot replace
        // the 1,000-game workload. Keep the earlier startup/idle metrics intact.
        var dashboard = Service("Steamy.ViewModels.DashboardViewModel");
        await ControlledMedia.SettleAsync();
        var initialRefreshWait = Stopwatch.StartNew();
        double settledCpuStartMs = 0;
        long settledCpuStart = 0;
        while (true)
        {
            while (Get(dashboard, "IsRefreshing") is true)
            {
                if (initialRefreshWait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Initial Dashboard library refresh did not settle before the stress fixture deadline.");
                await Task.Delay(25);
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            settledCpuStart = Stopwatch.GetTimestamp();
            using (var samplingProcess = Process.GetCurrentProcess()) settledCpuStartMs = samplingProcess.TotalProcessorTime.TotalMilliseconds;
            await Task.Delay(500);
            if (Get(dashboard, "IsRefreshing") is not true) break;
        }
        var settledIdle = Memory();
        var settledCpuIntervalMs = Stopwatch.GetElapsedTime(settledCpuStart).TotalMilliseconds;
        var settledIdleCpu = new { intervalMs = settledCpuIntervalMs, cpuMs = settledIdle.cpuMs - settledCpuStartMs,
            oneCorePercent = (settledIdle.cpuMs - settledCpuStartMs) / settledCpuIntervalMs * 100,
            scope = "CPU during the existing 500 ms initial-refresh settle interval; no extra polling timer. Public artwork/feed background work may still be active." };
        var settledIdleScope = new { initialRefreshWaitMs = initialRefreshWait.Elapsed.TotalMilliseconds,
            meaning = "500 ms after the initial Dashboard library refresh and its UI work completed; separate from the original 1.5-second early idle sample. Public artwork/feed requests may still be active." };
        MapNative("settled-startup");
        if (Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_STARTUP_ONLY") == "1")
        {
            ControlledMedia.Begin();
            var controlledMedia = ControlledMedia.Finish(dashboard);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { releaseExecutable = Environment.ProcessPath, firstVisibleMs, firstReadyMs, idle, settledIdle, settledIdleScope, settledIdleCpu, environment,
                helperSummary = HelperSummary([idle, settledIdle]), nativeMemory, controlledMedia, kind = "Warm startup, same isolated profile and filesystem cache" }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); return;
        }
        var syncType = assembly.GetType("Steamy.Services.ILibrarySyncService")!;
        var library = Service("Steamy.ViewModels.LibraryViewModel");
        foreach (var viewModel in new[] { dashboard, library })
            viewModel.GetType().GetField("_librarySync", Any)!.SetValue(viewModel, DispatchProxy.Create(syncType, typeof(OfflineRefreshProxy)));
        var games = (IList)Get(Service("Steamy.Services.IAppDataStore"), "Games")!;
        var gameType = assembly.GetType("Steamy.Models.Game")!;
        while (games.Count > 1000) games.RemoveAt(games.Count - 1);
        for (var i = games.Count; i < 1000; i++)
        {
            var game = Activator.CreateInstance(gameType)!; Set(game, "AppId", -10000 - i); Set(game, "Name", $"Benchmark game {i:0000}");
            Set(game, "InstallState", Enum.Parse(assembly.GetType("Steamy.Models.GameInstallState")!, "Installed")); games.Add(game);
        }
        Set(library, "SelectedMode", "Your games"); Set(library, "PageSize", 1000);
        var discordEnabled = (bool?)assembly.GetType("Steamy.Services.DiscordIntegration")?.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? true;
        IList messages = Array.Empty<object>();
        FrameworkElement? dock = null;
        TextBox composer;
        if (discordEnabled)
        {
        var model = Service("Steamy.ViewModels.DiscordFriendsViewModel");
        var profileType = assembly.GetType("Steamy.Services.DiscordProfile")!;
        var person = Activator.CreateInstance(profileType, [2UL, "fixture", "Performance friend", null])!;
        var friendType = assembly.GetType("Steamy.Services.DiscordFriend")!;
        var friend = Activator.CreateInstance(friendType, [person, Enum.Parse(assembly.GetType("Steamy.Services.DiscordRelationshipKind")!, "Friend"), "Online"])!;
        var friends = Array.CreateInstance(friendType, 1); friends.SetValue(friend, 0);
        var snapshot = Activator.CreateInstance(assembly.GetType("Steamy.Services.DiscordSnapshot")!, [true, person, friends]);
        var social = DispatchProxy.Create(assembly.GetType("Steamy.Services.IDiscordSocialService")!, typeof(OfflineRefreshProxy));
        ((OfflineRefreshProxy)social).Snapshot = snapshot; model.GetType().GetField("_discord", Any)!.SetValue(model, social); Call(model, "Refresh"); Set(model, "SelectedFriend", friend);
        Call(window, "OpenSocialDock"); await Task.Delay(100);
        dock = (FrameworkElement)Get(window, "SocialDockPage")!; Call(dock, "RevealConversation");
        messages = (IList)Get(model, "Messages")!;
        var messageType = assembly.GetType("Steamy.Services.DiscordChatMessage")!;
        messages.Clear();
        for (var i = 0; i < 2000; i++) messages.Add(Activator.CreateInstance(messageType,
            [98000UL + (ulong)i, 2UL, 2UL, 0UL, 0UL, "Offline benchmark message " + i, DateTimeOffset.Now.AddSeconds(i - 2000), false, false, false, "Performance friend", "", "", 0]));
        composer = (TextBox)dock.FindName("MessageComposer");
        }
        else
        {
            Call(window, "NavigateWithFeedback", assembly.GetType("Steamy.Pages.LibraryPage")!);
            var deadline = Stopwatch.StartNew();
            while (!Views<Page>(window).Any(page => page.GetType().Name == "LibraryPage" && page.IsVisible))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Library input target did not become visible.");
                await Task.Delay(25);
            }
            var page = Views<Page>(window).Single(page => page.GetType().Name == "LibraryPage" && page.IsVisible);
            composer = (TextBox)page.FindName("LibrarySearch");
        }
        await Task.Delay(300); window.UpdateLayout();
        ControlledMedia.Begin(games);
        var chatRealization = dock is null ? null : RealizedPanels(dock);
        var input = new List<double>();
        for (var i = 0; i < 120; i++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var began = Stopwatch.GetTimestamp();
            EventHandler? frame = null; frame = (_, _) => { CompositionTarget.Rendering -= frame; input.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds); completion.TrySetResult(); };
            CompositionTarget.Rendering += frame; composer.AppendText("x");
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); } finally { CompositionTarget.Rendering -= frame; }
            await Task.Delay(8);
        }
        if (discordEnabled) Call(window, "CloseSocialDock");
        else { composer.Clear(); await Task.Delay(300); window.UpdateLayout(); }
        composer = null!; // The observer must not retain the input page during the LRU measurement.
        var routes = discordEnabled
            ? new[] { "LibraryPage", "DownloadsPage", "ControllersPage", "SettingsPage", "FriendsHubPage", "DashboardPage" }
            : new[] { "LibraryPage", "DownloadsPage", "ControllersPage", "SettingsPage", "DashboardPage" };
        var navigation = new List<object>(); var memory = new List<MemorySample>(); MemorySample? retainedStart = null, retainedEnd = null;
        var gameCountsAfterCycles = new List<int>();
        var libraryRealization = new List<object>();
        var visitedPages = new List<WeakReference<Page>>();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            foreach (var route in routes)
            {
                var allocatedBeforeNavigation = GC.GetTotalAllocatedBytes();
                var began = Stopwatch.GetTimestamp(); Call(window, "NavigateWithFeedback", assembly.GetType("Steamy.Pages." + route)!);
                var queuedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                var confirmed = false;
                // A slow baseline is still a measurement, not an absent route.
                // Bound the wait by wall time rather than 100 dispatcher polls
                // (roughly one second on an otherwise idle UI thread).
                var routeDeadline = Stopwatch.StartNew();
                while (routeDeadline.Elapsed < TimeSpan.FromSeconds(30))
                { await Task.Delay(25); if (Views<Page>(window).Any(page => page.GetType().Name == route && page.IsVisible)) { confirmed = true; break; } }
                if (!confirmed) throw new TimeoutException($"Route {route} did not become visible in cycle {cycle}.");
                var visibleMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                await window.Dispatcher.InvokeAsync(() => { window.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                var elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                navigation.Add(new { cycle, route, elapsedMs, queuedMs, visibleMs,
                    afterVisibleMs = elapsedMs - visibleMs, allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBeforeNavigation });
                foreach (var page in Views<Page>(window).Where(page => page.GetType().Name == route && page.IsVisible))
                    if (!visitedPages.Any(reference => reference.TryGetTarget(out var previous) && ReferenceEquals(previous, page))) visitedPages.Add(new(page));
                if (route == "LibraryPage" && cycle is 0 or 9) libraryRealization.Add(new { cycle, panels = RealizedPanels(window) });
                if (route == "LibraryPage") foreach (var scroll in Views<ScrollViewer>(window).Where(view => view.Name == "GalleryScroll")) { scroll.ScrollToEnd(); window.UpdateLayout(); scroll.ScrollToTop(); }
                if (cycle == 0 && route is "LibraryPage" or "FriendsHubPage" or "DashboardPage") await ControlledMedia.DrainRecordingAsync();
            }
            gameCountsAfterCycles.Add(games.Count);
            if (games.Count != 1000) throw new InvalidOperationException($"The stress dataset changed in cycle {cycle}: expected 1000 games, found {games.Count}.");
            memory.Add(Memory());
            if (cycle is 0 or 9)
            {
                // Diagnostic collection only, never used by Steamy during navigation.
                // Separates retained objects from work awaiting a normal GC cycle.
                await Task.Delay(500); GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
                await Task.Delay(500); GC.Collect(2, GCCollectionMode.Forced, true, true);
                if (cycle == 0) retainedStart = Memory(); else retainedEnd = Memory();
                MapNative(cycle == 0 ? "retained-cycle-0" : "retained-cycle-9");
            }
        }
        var imageTimes = new List<double>(); var artwork = Service("Steamy.Services.IArtworkService");
        for (var i = 0; i < 20; i++)
        { var began = Stopwatch.GetTimestamp(); await (Task)artwork.GetType().GetMethod("LoadHeroAsync")!.Invoke(artwork, [1808500, CancellationToken.None])!; imageTimes.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds); }
        var diagnoseMinimize = Environment.GetEnvironmentVariable("STEAMY_PERFORMANCE_EXTENDED_IDLE") == "1";
        var minimizeThreadsBefore = diagnoseMinimize ? ThreadTimes() : null;
        var beforeMinimize = Memory(); var minimizeWatch = Stopwatch.StartNew();
        window.WindowState = WindowState.Minimized; await Task.Delay(2000); var minimized = Memory();
        var minimizeElapsedMs = minimizeWatch.Elapsed.TotalMilliseconds;
        var minimizeThreadsAfter = diagnoseMinimize ? ThreadTimes() : null;
        var pipelineType = assembly.GetType("Steamy.Services.ImagePipeline");
        var pipeline = pipelineType?.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var imageCacheBytes = pipeline is null ? (long?)null : (long?)Get(pipeline, "CachedBytes");
        var extendedIdle = await ExtendedIdleAsync(window);
        MapNative("after-idle-diagnostics");
        var settingsConstruction = await MeasureSettingsConstructionAsync(assembly);
        // Additional diagnostics run after every comparable legacy timing/memory point.
        // Creating fresh image loaders must not inflate the retained-navigation sample.
        var controlledMediaResult = ControlledMedia.Finish(dashboard);
        object freshDiskImages;
        try { freshDiskImages = await MeasureFreshDiskImagesAsync(assembly); }
        catch (Exception error) { freshDiskImages = new { available = false, error = error.ToString() }; }
        object networkImage;
        try { networkImage = await MeasureNetworkImageAsync(assembly); }
        catch (Exception error) { networkImage = new { attempted = true, loaded = false, error = error.ToString() }; }
        var processSamples = new[] { idle, settledIdle }.Concat(memory).Concat(new[] { retainedStart!, retainedEnd!, beforeMinimize, minimized });
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            releaseExecutable = Environment.ProcessPath, firstVisibleMs, firstReadyMs, idle, settledIdle, settledIdleScope, settledIdleCpu, environment, games = games.Count, messages = messages.Count,
            discordEnabled, inputTarget = discordEnabled ? "Offline chat composer" : "Library search textbox", navigationRoutes = routes,
            inputToRenderP95Ms = input.Order().ElementAt(113), navigation, memoryAfterCycles = memory, retainedStart, retainedEnd, beforeMinimize, minimized,
            associatedPages = Get(window.FindName("RootNavigation"), "AssociatedPageCount"),
            cachedImageTimesMs = imageTimes, imageCacheBytes,
            sharedImageLoads = new { scope = "Twenty sequential calls to the shared artwork service. The first may already be in memory; repeats primarily measure memory-cache hits, not filesystem reads.", firstMs = imageTimes[0], repeatTimesMs = imageTimes.Skip(1).ToArray() },
            freshDiskImages, networkImage, extendedIdle, nativeMemory, settingsConstruction, controlledMedia = controlledMediaResult, helperSummary = HelperSummary(processSamples), chatRealization, libraryRealization, gameCountsAfterCycles,
            visitedMainPagesStillAlive = visitedPages.Count(reference => reference.TryGetTarget(out _)),
            visitedMainPageTypesStillAlive = visitedPages.Select(reference => reference.TryGetTarget(out var page) ? page.GetType().Name : null)
                .Where(name => name is not null).GroupBy(name => name).Select(group => new { type = group.Key, count = group.Count() }).ToArray(),
            datasetStable = gameCountsAfterCycles.All(count => count == 1000),
            memoryScope = "managedBytes uses GC.GetTotalMemory(false); gcCommittedBytes/gcHeapSizeBytes/gcFragmentedBytes describe the last available GC memory-info snapshot, not a live native-allocation census. Private Bytes minus managed live bytes includes committed GC free space/fragmentation and runtime/JIT/allocator high-water, as well as native/image/composition memory; that difference alone does not prove a native leak.",
            minimizedThreads = minimizeThreadsAfter?.Select(thread => new { thread.id, thread.name,
                cpuMs = thread.cpuMs - (minimizeThreadsBefore!.FirstOrDefault(previous => previous.id == thread.id)?.cpuMs ?? thread.cpuMs) })
                .OrderByDescending(thread => thread.cpuMs),
            minimizedCpu = new { intervalMs = minimized.sampledAtMs - beforeMinimize.sampledAtMs, monotonicIntervalMs = minimizeElapsedMs,
                cpuMs = minimized.cpuMs - beforeMinimize.cpuMs,
                oneCorePercent = (minimized.cpuMs - beforeMinimize.cpuMs) / (minimized.sampledAtMs - beforeMinimize.sampledAtMs) * 100 },
            limitations = "Actual Release Steamy.exe, opt-in startup diagnostic hook; isolated profile and synthetic local games. Discord-enabled builds additionally use 2000 synthetic messages; removed builds measure the Library search textbox and omit social routes. These workloads cannot be compared with each other. The initial Dashboard scan reads installed Steam metadata and completes before fixtures are injected. Subsequent Dashboard/Library refresh is offline. Pinned public HTTP responses apply only when controlledMedia.controlled is true; otherwise public network variability invalidates a controlled comparison. Input measures the next WPF rendering callback, not physical latency or completed debounced search. No real login, sent messages or controller changes. Cold samples are process-cold; OS filesystem cache is not flushed. Early idle is 1.5 seconds after UI-ready; settledIdle additionally waits for initial library refresh, not all background network requests."
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
