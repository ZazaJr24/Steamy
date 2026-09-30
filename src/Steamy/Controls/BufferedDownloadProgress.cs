using System.Windows.Threading;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>At most two UI updates per tick, even when the downloader emits thousands of lines.</summary>
public sealed class BufferedDownloadProgress : IProgress<string>, IDisposable
{
    private readonly object _gate = new();
    private readonly DispatcherTimer _timer;
    private readonly Action<string> _apply;
    private string? _status;
    private string? _progress;
    private bool _disposed;

    public BufferedDownloadProgress(Action<string> apply)
    {
        _apply = apply;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Report(string value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (value.StartsWith(GameDownloadProgressMessage.Prefix, StringComparison.Ordinal)) _progress = value;
            else _status = value;
        }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        string? status, progress;
        lock (_gate) { status = _status; progress = _progress; _status = _progress = null; }
        if (status is not null) _apply(status);
        if (progress is not null) _apply(progress);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _status = _progress = null; }
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
