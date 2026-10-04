using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

/// <summary>Disk writes are used only to detect activity, never as transferred bytes.</summary>
public static class DownloadByteSource
{
    public static Func<long?> For(Process process, string targetFolder) => ProcessWriteCounter.For(process);
}

/// <summary>Tracks actual tool telemetry. Older tools retain their reported per-depot percentage.</summary>
public sealed class DownloadProgressTracker
{
    private readonly object _gate = new();
    private readonly Func<long?> _activityBytes;
    private readonly Func<double> _clock;
    private readonly HashSet<string> _depots = new(StringComparer.Ordinal);
    private DownloadTelemetry? _telemetry;
    private readonly Dictionary<int, DownloadTelemetry> _depotTelemetry = new();
    private DepotDownloaderProgress? _legacy;
    private double _lastActivity, _lastSample, _rate;
    private long _lastTransferred, _lastWrites;
    private int _depotIndex;
    private string _pending = "";
    public DownloadProgressTracker(Func<long?> bytesWritten, Func<double>? clockSeconds = null)
    {
        _activityBytes = bytesWritten;
        var stopwatch = Stopwatch.StartNew();
        _clock = clockSeconds ?? (() => stopwatch.Elapsed.TotalSeconds);
    }
    public double SecondsSinceActivity
    {
        get { lock (_gate) { var written = _activityBytes(); if (written > _lastWrites) { _lastWrites = written.Value; _lastActivity = _clock(); } return _clock() - _lastActivity; } }
    }
    public DepotDownloaderProgress? ObserveLine(string line)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!string.IsNullOrWhiteSpace(line)) _lastActivity = now;
            if (line.StartsWith(DownloadTelemetry.Prefix, StringComparison.Ordinal))
            {
                var value = DownloadTelemetry.Parse(line);
                if (value is null) return null;
                if (_telemetry?.DepotId != value.DepotId) { _rate = 0; _lastTransferred = 0; _lastSample = now; }
                var elapsed = now - _lastSample;
                if (elapsed >= 0.25)
                {
                    var instant = Math.Max(0, value.TransferredBytes - _lastTransferred) / elapsed;
                    _rate += (instant - _rate) * (1 - Math.Exp(-elapsed / 3));
                    _lastSample = now; _lastTransferred = value.TransferredBytes;
                }
                _telemetry = value;
                _depotTelemetry[value.DepotId] = value;
                return Snapshot();
            }
            var processing = Regex.Match(line, @"^\s*Processing depot (?<id>\d+)");
            if (processing.Success) _depots.Add(processing.Groups["id"].Value);
            if (Regex.IsMatch(line, @"^\s*Downloading depot \d+\s*$")) { _depotIndex++; _legacy = null; }
            var parsed = DepotDownloaderOutputParser.Parse(line);
            if (parsed is not null) { _legacy = parsed; _pending = line; }
            return parsed;
        }
    }
    public DepotDownloaderProgress Snapshot()
    {
        lock (_gate)
        {
            var raw = _pending; _pending = "";
            if (_telemetry is { } value)
            {
                var rate = _clock() - _lastSample > 5 ? 0 : _rate;
                double? eta = value.Phase == "downloading" && value.TransferTotalBytes is { } total && rate > 0
                    && _clock() >= 2 ? Math.Max(0, total - value.TransferredBytes) / rate : null;
                var allKnown = _depotTelemetry.Count >= Math.Max(_depots.Count, 1);
                var totalContent = _depotTelemetry.Values.Sum(depot => (double)depot.TotalBytes);
                var content = _depotTelemetry.Values.Sum(depot => (double)depot.ContentBytes);
                var transferred = _depotTelemetry.Values.Sum(depot => (double)depot.TransferredBytes);
                var reused = _depotTelemetry.Values.Sum(depot => (double)depot.ReusedBytes);
                long? transferTotal = allKnown && _depotTelemetry.Values.All(depot => depot.TransferTotalBytes is not null)
                    ? (long?)Math.Min(long.MaxValue - 1.0, _depotTelemetry.Values.Sum(depot => (double)depot.TransferTotalBytes!.Value)) : null;
                if (!allKnown || _depotTelemetry.Count > 1) eta = null;
                // When several depots are present the aggregate is intentionally withheld until
                // every depot has reported its real size. In the meantime report the current
                // depot's own measured fraction, the same useful per-depot signal the tool emits.
                var depotProgressPercent = value.Phase != "downloading" ? (double?)null
                    : allKnown && totalContent > 0 ? Math.Min(99.9, content * 100.0 / totalContent)
                    : value.TotalBytes > 0 ? Math.Min(99.9, value.ContentBytes * 100.0 / value.TotalBytes)
                    : null;
                return new(depotProgressPercent,
                    _legacy?.CurrentFile ?? "", DownloadFormat.Bytes((long)Math.Min(transferred, long.MaxValue - 1.0)),
                    transferTotal is { } size ? DownloadFormat.Bytes(size) : "Unknown",
                    value.Phase == "downloading" ? DownloadFormat.Speed(rate) : "", eta is { } seconds ? "~" + DownloadFormat.Duration(seconds) : "", raw,
                    Math.Max(_depotIndex, 1), Math.Max(_depots.Count, 1), rate, eta, (long)Math.Min(transferred, long.MaxValue - 1.0),
                    (long)Math.Min(totalContent, long.MaxValue - 1.0), transferTotal,
                    (long)Math.Min(content, long.MaxValue - 1.0), (long)Math.Min(reused, long.MaxValue - 1.0), value.Phase);
            }
            // No interpolation from time, folder growth or process writes. Multi-depot totals
            // are unknown until actual weights are available.
            return new(_depots.Count <= 1 ? _legacy?.Percent is { } percent && double.IsFinite(percent) ? Math.Clamp(percent, 0, 99.9) : null : null,
                _legacy?.CurrentFile ?? "", _legacy?.Downloaded ?? "", _legacy?.Total ?? "",
                _legacy?.Speed ?? "", _legacy?.Eta ?? "", raw, _depotIndex, _depots.Count);
        }
    }
}

public static class DownloadFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.00} TiB"),
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} GiB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
    };

    public static string Speed(double bytesPerSecond) => double.IsFinite(bytesPerSecond) && bytesPerSecond >= 0 && bytesPerSecond < long.MaxValue ? Bytes((long)bytesPerSecond) + "/s" : "—";

    /// <summary>Reads a formatted size such as "473.4 MiB" back into bytes; 0 when it cannot.</summary>
    public static long TryParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var value = text.Trim().TrimStart('~').Trim();
        var digits = 0;
        while (digits < value.Length && (char.IsDigit(value[digits]) || value[digits] is '.' or ',')) digits++;
        if (digits == 0) return 0;
        if (!double.TryParse(value[..digits].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return 0;

        var unit = value[digits..].Trim().ToUpperInvariant();
        var factor = (unit.StartsWith("TB", StringComparison.Ordinal) || unit.StartsWith("TIB", StringComparison.Ordinal)) ? 1L << 40
            : (unit.StartsWith("GB", StringComparison.Ordinal) || unit.StartsWith("GIB", StringComparison.Ordinal)) ? 1L << 30
            : (unit.StartsWith("MB", StringComparison.Ordinal) || unit.StartsWith("MIB", StringComparison.Ordinal)) ? 1L << 20
            : (unit.StartsWith("KB", StringComparison.Ordinal) || unit.StartsWith("KIB", StringComparison.Ordinal)) ? 1L << 10
            : 1L;
        return double.IsFinite(number) && number >= 0 && number * factor < long.MaxValue ? (long)(number * factor) : 0;
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds - 1) return string.Empty;
        var time = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds:00}s";
        return $"{time.Seconds}s";
    }
}
