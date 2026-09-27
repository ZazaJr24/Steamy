using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>Reads how many bytes a running process has written to disk so far.</summary>
public static class ProcessWriteCounter
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    public static Func<long?> For(Process process) => () =>
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (process.HasExited) return null;
            return GetProcessIoCounters(process.Handle, out var counters) ? (long)counters.WriteTransferCount : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    };
}

/// <summary>
/// Turns DepotDownloader's coarse output into a continuous progress value. The tool prints a
/// percentage only when a whole file is finished and restarts at 0 % for every depot, so a single
/// large file shows as one big jump. Between those lines the tracker interpolates from the bytes
/// the process has actually written, and it folds the per-depot percentages into one overall value
/// that never moves backwards.
/// </summary>
public sealed class DownloadProgressTracker
{
    private const double MaxInterpolatedPercent = 99.9;
    private const long MinimumSampleBytes = 1L << 20;
    private const double SpeedSmoothingSeconds = 1.5;

    private static readonly Regex ProcessingDepotPattern = new(@"^\s*Processing depot (?<id>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DownloadingDepotPattern = new(@"^\s*Downloading depot (?<id>\d+)\b(?!\s+manifest)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Func<long?> _bytesWritten;
    private readonly Func<double> _clock;

    private readonly HashSet<string> _depots = new(StringComparer.Ordinal);
    private int _depotIndex;

    private double _anchorPercent;
    private long _anchorBytes;
    private double _anchorTime;
    private bool _hasPercent;
    private double? _bytesPerPercent;
    private double? _percentPerSecond;
    private double _lastStep;
    private double _displayed;

    private long _lastBytes;
    private double _speedSampleTime;
    private long _speedSampleBytes;
    private double _bytesPerSecond;
    private double _lastActivity;

    private string _currentFile = string.Empty;
    private string _toolDownloaded = string.Empty;
    private string _toolTotal = string.Empty;
    private string _toolSpeed = string.Empty;
    private string _toolEta = string.Empty;
    private string _pendingLine = string.Empty;

    public DownloadProgressTracker(Func<long?> bytesWritten, Func<double>? clockSeconds = null)
    {
        _bytesWritten = bytesWritten;
        if (clockSeconds is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _clock = () => stopwatch.Elapsed.TotalSeconds;
        }
        else
        {
            _clock = clockSeconds;
        }
    }

    /// <summary>Seconds since the last output line or the last byte written.</summary>
    public double SecondsSinceActivity
    {
        get
        {
            lock (_gate)
            {
                ReadBytes(_clock());
                return _clock() - _lastActivity;
            }
        }
    }

    /// <summary>Feeds one line of tool output. Returns the parsed progress line, or null for other output.</summary>
    public DepotDownloaderProgress? ObserveLine(string line)
    {
        var parsed = DepotDownloaderOutputParser.Parse(line);

        lock (_gate)
        {
            var now = _clock();
            var bytes = ReadBytes(now);
            _lastActivity = now;
            if (string.IsNullOrWhiteSpace(line)) return parsed;

            var processing = ProcessingDepotPattern.Match(line);
            if (processing.Success) _depots.Add(processing.Groups["id"].Value);

            var downloading = DownloadingDepotPattern.Match(line);
            if (downloading.Success)
            {
                _depots.Add(downloading.Groups["id"].Value);
                StartSegment(now, bytes);
            }

            if (parsed is null) return null;

            _pendingLine = line;
            if (!string.IsNullOrWhiteSpace(parsed.CurrentFile)) _currentFile = parsed.CurrentFile;
            if (!string.IsNullOrWhiteSpace(parsed.Downloaded)) _toolDownloaded = parsed.Downloaded;
            if (!string.IsNullOrWhiteSpace(parsed.Total)) _toolTotal = parsed.Total;
            if (!string.IsNullOrWhiteSpace(parsed.Speed)) _toolSpeed = parsed.Speed;
            if (!string.IsNullOrWhiteSpace(parsed.Eta)) _toolEta = parsed.Eta;
            if (parsed.Percent is { } percent) ObservePercent(percent, now, bytes);

            return parsed;
        }
    }

    /// <summary>The current, interpolated state. Percent is null until anything measurable happened.</summary>
    public DepotDownloaderProgress Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            var bytes = ReadBytes(now);
            UpdateSpeed(now, bytes);

            var depotCount = Math.Max(Math.Max(_depots.Count, _depotIndex), 1);
            var depotIndex = Math.Clamp(Math.Max(_depotIndex, 1), 1, depotCount);
            var segment = SegmentPercent(now, bytes);
            var overall = ((depotIndex - 1) * 100.0 + segment) / depotCount;
            _displayed = Math.Max(_displayed, Math.Round(overall, 2));

            var started = _hasPercent || _depotIndex > 0;
            var speed = _toolSpeed.Length > 0 ? _toolSpeed
                : _bytesPerSecond > 1 ? DownloadFormat.Speed(_bytesPerSecond) : string.Empty;

            var downloaded = _toolDownloaded.Length > 0 ? _toolDownloaded
                : bytes > 0 ? DownloadFormat.Bytes(bytes) : string.Empty;

            var total = _toolTotal;
            var eta = _toolEta;
            double? etaSeconds = null;
            if (_bytesPerPercent is { } bytesPerPercent && bytesPerPercent > 0)
            {
                if (total.Length == 0 && depotCount == 1) total = "~" + DownloadFormat.Bytes((long)(bytesPerPercent * 100));
                if (_bytesPerSecond > 1)
                {
                    etaSeconds = (100 - segment) * bytesPerPercent / _bytesPerSecond;
                    if (eta.Length == 0)
                    {
                        eta = depotCount > 1
                            ? $"{DownloadFormat.Duration(etaSeconds.Value)} for depot {depotIndex} of {depotCount}"
                            : $"{DownloadFormat.Duration(etaSeconds.Value)} left";
                    }
                }
            }

            var rawLine = _pendingLine;
            _pendingLine = string.Empty;

            return new DepotDownloaderProgress(
                started ? _displayed : null,
                _currentFile,
                downloaded,
                total,
                speed,
                eta,
                rawLine,
                depotIndex,
                depotCount,
                _bytesPerSecond,
                etaSeconds);
        }
    }

    private void StartSegment(double now, long bytes)
    {
        _depotIndex++;
        _anchorPercent = 0;
        _anchorBytes = bytes;
        _anchorTime = now;
        _hasPercent = false;
        _bytesPerPercent = null;
        _percentPerSecond = null;
        _lastStep = 0;
        _currentFile = string.Empty;
    }

    private void ObservePercent(double percent, double now, long bytes)
    {
        if (_depotIndex == 0) StartSegment(now, 0);

        var step = percent - _anchorPercent;
        if (step > 0)
        {
            _lastStep = step;

            var elapsed = now - _anchorTime;
            if (elapsed > 0.05)
                _percentPerSecond = Blend(_percentPerSecond, step / elapsed, 0.3);

            // A step that wrote almost nothing belongs to a file that was already on disk
            // (resume / validation). Such samples would make the estimate race ahead.
            var deltaBytes = bytes - _anchorBytes;
            if (deltaBytes >= MinimumSampleBytes)
            {
                var sample = deltaBytes / step;
                if (_bytesPerPercent is not { } current || sample >= current * 0.25)
                    _bytesPerPercent = Blend(_bytesPerPercent, sample, 0.35);
            }
        }

        _anchorPercent = percent;
        _anchorBytes = bytes;
        _anchorTime = now;
        _hasPercent = true;
    }

    private double SegmentPercent(double now, long bytes)
    {
        if (!_hasPercent && _depotIndex == 0) return 0;

        var estimate = _anchorPercent;
        if (_bytesPerPercent is { } bytesPerPercent && bytesPerPercent > 0 && bytes > _anchorBytes)
        {
            estimate += (bytes - _anchorBytes) / bytesPerPercent;
        }
        else if (_percentPerSecond is { } rate && rate > 0)
        {
            // Without byte counters, advance by time but never further than the last observed step.
            var lead = Math.Min(rate * (now - _anchorTime), Math.Max(_lastStep, 0.5));
            estimate += lead;
        }

        return Math.Clamp(estimate, _anchorPercent, Math.Max(_anchorPercent, MaxInterpolatedPercent));
    }

    private long ReadBytes(double now)
    {
        var bytes = _bytesWritten();
        if (bytes is { } value && value > _lastBytes)
        {
            _lastBytes = value;
            _lastActivity = now;
        }

        return _lastBytes;
    }

    private void UpdateSpeed(double now, long bytes)
    {
        var elapsed = now - _speedSampleTime;
        if (elapsed < 0.25) return;

        var instant = Math.Max(0, bytes - _speedSampleBytes) / elapsed;
        var weight = 1 - Math.Exp(-elapsed / SpeedSmoothingSeconds);
        _bytesPerSecond = _speedSampleTime == 0 ? instant : _bytesPerSecond + (instant - _bytesPerSecond) * weight;
        _speedSampleTime = now;
        _speedSampleBytes = bytes;
    }

    private static double Blend(double? current, double sample, double weight) =>
        current is { } value ? value + (sample - value) * weight : sample;
}

public static class DownloadFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.00} TB"),
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
    };

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return string.Empty;
        var time = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds:00}s";
        return $"{time.Seconds}s";
    }
}
