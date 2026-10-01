using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>
/// Records slow UI responses locally. The background timer never queues more than one probe,
/// and stops while the window is inactive, minimized or closed.
/// </summary>
public sealed class UiResponsivenessMonitor : IDisposable
{
    private readonly Window _window;
    private readonly ILoggingService _logging;
    private readonly System.Threading.Timer _timer;
    private int _active;
    private int _pending;
    private int _epoch;
    private int _disposed;
    private long _lastWarning;

    public UiResponsivenessMonitor(Window window, ILoggingService logging)
    {
        _window = window;
        _logging = logging;
        _timer = new System.Threading.Timer(Probe, null, Timeout.Infinite, Timeout.Infinite);
        window.Activated += OnWindowStateChanged;
        window.Deactivated += OnWindowStateChanged;
        window.StateChanged += OnWindowStateChanged;
        window.Closed += OnWindowClosed;
        UpdateActivity();
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateActivity();

    private void UpdateActivity()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var active = _window.IsActive && _window.IsVisible && _window.WindowState != WindowState.Minimized;
        Interlocked.Exchange(ref _active, active ? 1 : 0);
        Interlocked.Increment(ref _epoch);
        _timer.Change(active ? 2000 : Timeout.Infinite, active ? 2000 : Timeout.Infinite);
    }

    private void Probe(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _active) == 0
            || Interlocked.Exchange(ref _pending, 1) != 0) return;
        var started = Stopwatch.GetTimestamp();
        var epoch = Volatile.Read(ref _epoch);
        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RecordResponse(started, epoch));
        }
        catch
        {
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    private void RecordResponse(long started, int epoch)
    {
        Interlocked.Exchange(ref _pending, 0);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _active) == 0
            || epoch != Volatile.Read(ref _epoch)) return;
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed < TimeSpan.FromMilliseconds(300)) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastWarning != 0 && Stopwatch.GetElapsedTime(_lastWarning, now) < TimeSpan.FromSeconds(30)) return;
        _lastWarning = now;
        try
        {
            using var process = Process.GetCurrentProcess();
            _logging.Add(LogLevel.Warning, "Performance",
                $"UI response delayed by {elapsed.TotalMilliseconds:F0} ms. "
                + $"Managed memory: {GC.GetTotalMemory(false) / 1048576d:F0} MiB; "
                + $"working set: {process.WorkingSet64 / 1048576d:F0} MiB.");
        }
        catch
        {
            // Diagnostics must never add another failure to an already busy UI.
        }
    }

    private void OnWindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _active, 0);
        _timer.Dispose();
        _window.Activated -= OnWindowStateChanged;
        _window.Deactivated -= OnWindowStateChanged;
        _window.StateChanged -= OnWindowStateChanged;
        _window.Closed -= OnWindowClosed;
    }
}
