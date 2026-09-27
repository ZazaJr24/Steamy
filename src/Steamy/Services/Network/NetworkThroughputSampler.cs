using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Steamy.Services;

/// <summary>
/// Measures how fast this machine is receiving data from the network. The rate is taken from the
/// busiest adapter instead of the sum of all adapters: traffic that flows through a VPN or Hyper-V
/// switch also shows up on the physical card, and adding both would double the real speed.
/// </summary>
public sealed class NetworkThroughputSampler
{
    private const double SmoothingSeconds = 1.0;

    private readonly Func<IReadOnlyDictionary<string, long>?> _readReceivedBytes;
    private readonly Func<double> _clock;
    private readonly Queue<double> _history = new();
    private Dictionary<string, long>? _lastBytes;
    private double _lastTime;

    public NetworkThroughputSampler(
        Func<IReadOnlyDictionary<string, long>?>? readReceivedBytes = null,
        Func<double>? clockSeconds = null,
        int historyLength = 60)
    {
        _readReceivedBytes = readReceivedBytes ?? ReadReceivedBytes;
        if (clockSeconds is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _clock = () => stopwatch.Elapsed.TotalSeconds;
        }
        else
        {
            _clock = clockSeconds;
        }

        HistoryLength = historyLength;
    }

    public int HistoryLength { get; }
    public double BytesPerSecond { get; private set; }
    public double PeakBytesPerSecond { get; private set; }
    public IReadOnlyCollection<double> History => _history;

    public double Sample()
    {
        var now = _clock();
        var current = _readReceivedBytes();
        if (current is null) return BytesPerSecond;

        if (_lastBytes is not null && now > _lastTime)
        {
            var elapsed = now - _lastTime;
            var busiest = 0L;
            foreach (var (adapter, bytes) in current)
            {
                if (_lastBytes.TryGetValue(adapter, out var previous) && bytes > previous)
                    busiest = Math.Max(busiest, bytes - previous);
            }

            var instant = busiest / elapsed;
            var weight = 1 - Math.Exp(-elapsed / SmoothingSeconds);
            BytesPerSecond = _history.Count == 0 ? instant : BytesPerSecond + (instant - BytesPerSecond) * weight;
            PeakBytesPerSecond = Math.Max(PeakBytesPerSecond, BytesPerSecond);

            _history.Enqueue(BytesPerSecond);
            while (_history.Count > HistoryLength) _history.Dequeue();
        }

        _lastBytes = new Dictionary<string, long>(current);
        _lastTime = now;
        return BytesPerSecond;
    }

    public static IReadOnlyDictionary<string, long>? ReadReceivedBytes()
    {
        try
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                result[adapter.Id] = adapter.GetIPStatistics().BytesReceived;
            }

            return result;
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
