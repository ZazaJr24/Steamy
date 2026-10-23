using System.Globalization;
using System.Text.Json.Serialization;
using Steamy.Services;

namespace Steamy.Models;

public sealed partial class DownloadJob
{
    private double _sessionPeak;
    private readonly object _rateHistoryGate = new();
    private readonly List<double> _rateHistory = new(60);
    private double _lastRateSample;
    internal void RecordTransferRate(double rate, double? sampleSeconds = null)
    {
        if (!double.IsFinite(rate)) rate = 0;
        rate = Math.Max(0, rate);
        lock (_rateHistoryGate)
        {
            _sessionPeak = Math.Max(_sessionPeak, rate);
            var now = sampleSeconds ?? Environment.TickCount64 / 1000d;
            if (_rateHistory.Count == 0 || now - _lastRateSample >= 1)
            {
                _rateHistory.Add(rate); _lastRateSample = now;
                if (_rateHistory.Count > 60) _rateHistory.RemoveAt(0);
            }
            else _rateHistory[^1] = rate;
        }
    }
    internal double[] GetTransferRateHistory() { lock (_rateHistoryGate) return _rateHistory.ToArray(); }
    private static readonly string[] DeckProperties =
    [nameof(DeckNetworkRate), nameof(DeckDiskRate), nameof(SessionPeakBytesPerSecond),
     nameof(DeckTransferProgress), nameof(DeckContentProgress), nameof(DeckTransferIndeterminate),
     nameof(DeckContentIndeterminate), nameof(DeckTransferLabel), nameof(DeckContentLabel),
     nameof(DeckContentCaption), nameof(DeckTransferSummary), nameof(DeckQueueSize), nameof(DeckRemaining), nameof(DeckSection)];

    public DownloadJob()
    {
        // The presentation follows telemetry without changing persisted download state.
        PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is not (nameof(State) or nameof(Phase) or nameof(BytesPerSecond)
                or nameof(DiskSpeed) or nameof(Progress) or nameof(HasMeasuredProgress) or nameof(HasWholeGameProgress)
                or nameof(TransferredBytes) or nameof(TransferTotalBytes) or nameof(ContentBytes)
                or nameof(InstallationBytes) or nameof(Downloaded) or nameof(TotalSize)
                or nameof(EtaSeconds) or nameof(Eta) or nameof(CurrentDepotDetail))) return;
            foreach (var property in DeckProperties) OnPropertyChanged(property);
            OnPropertyChanged(nameof(DeckProgress)); OnPropertyChanged(nameof(DeckProgressIndeterminate)); OnPropertyChanged(nameof(DeckProgressCaption));
        };
    }

    public static string FormatBitrate(double bytesPerSecond) => !double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0 ? "—"
        : bytesPerSecond * 8 >= 1_000_000_000
            ? (bytesPerSecond * 8 / 1_000_000_000).ToString("0.0", CultureInfo.CurrentCulture) + " Gbit/s"
            : (bytesPerSecond * 8 / 1_000_000).ToString("0.0", CultureInfo.CurrentCulture) + " Mbit/s";
    private static string Size(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1_000_000_000d).ToString("0.00", CultureInfo.CurrentCulture) + " GB"
        : bytes >= 1_000_000 ? (bytes / 1_000_000d).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
        : DownloadFormat.Bytes(bytes);

    [JsonIgnore] public double SessionPeakBytesPerSecond { get { lock (_rateHistoryGate) return _sessionPeak; } }
    [JsonIgnore] public string DeckSection => IsTerminal ? "History" : "Queue";
    [JsonIgnore] public string DeckNetworkRate => IsActive ? FormatBitrate(BytesPerSecond) : "—";
    [JsonIgnore] public string DeckDiskRate => !IsActive || string.IsNullOrWhiteSpace(DiskSpeed) ? "—"
        : DownloadFormat.TryParseSize(DiskSpeed) is > 0 and var rate ? FormatBitrate(rate) : "0.0 Mbit/s";
    [JsonIgnore] public double DeckTransferProgress => State == DownloadJobState.Completed ? 100
        : TransferTotalBytes is > 0 ? Math.Clamp(TransferredBytes * 100d / TransferTotalBytes.Value, 0, 100) : 0;
    // The download header shows measured content progress even when the compressed
    // network total is unavailable. Never present an unknown total as a frozen 0%.
    [JsonIgnore] public double DeckProgress => State == DownloadJobState.Completed ? 100
        : HasMeasuredProgress ? Progress : DeckTransferProgress;
    [JsonIgnore] public bool DeckProgressIndeterminate => IsActive
        && (!HasMeasuredProgress && TransferTotalBytes is not > 0 || Phase is "checking" or "finalizing" || State is DownloadJobState.Preparing or DownloadJobState.Verifying);
    [JsonIgnore] public string DeckProgressCaption => State == DownloadJobState.Completed ? "100 %"
        : IsActive && (Phase is "checking" or "finalizing" || State is DownloadJobState.Preparing or DownloadJobState.Verifying) ? PhaseLabel + "…"
        : !HasMeasuredProgress && TransferTotalBytes is not > 0 ? IsActive ? "Measuring…" : "—"
        : DeckProgress.ToString("0.#", CultureInfo.CurrentCulture) + " %"
            + (HasMeasuredProgress && !HasWholeGameProgress && CurrentDepotDetail.StartsWith("Depot ", StringComparison.Ordinal)
                ? " · " + CurrentDepotDetail.Split('·')[0].Trim() : "");
    [JsonIgnore] public double DeckContentProgress => State == DownloadJobState.Completed ? 100
        : InstallationBytes > 0 ? Math.Clamp(ContentBytes * 100d / InstallationBytes, 0, 100) : 0;
    [JsonIgnore] public bool DeckTransferIndeterminate => IsActive && Phase != "finalizing" && TransferTotalBytes is not > 0;
    [JsonIgnore] public bool DeckContentIndeterminate => IsActive && InstallationBytes <= 0 && Phase is "checking" or "finalizing";
    [JsonIgnore] public string DeckTransferLabel => IsPaused ? "Download paused"
        : State == DownloadJobState.Preparing ? "Preparing download…"
        : State == DownloadJobState.Completed ? "Data downloaded"
        : State == DownloadJobState.Failed ? "Download failed" : "Downloading data…";
    [JsonIgnore] public string DeckContentLabel => (Phase == "checking" || State == DownloadJobState.Verifying
        ? "Checking existing files…" : State == DownloadJobState.Completed ? "Files updated" : "Updating files…")
        + (CurrentDepotDetail.StartsWith("Depot ", StringComparison.Ordinal) ? " · " + CurrentDepotDetail.Split('·')[0].Trim() : "");
    [JsonIgnore] public string DeckContentCaption => InstallationBytes > 0 || State == DownloadJobState.Completed
        ? DeckContentProgress.ToString("0", CultureInfo.CurrentCulture) + " %"
        : DeckContentIndeterminate ? "Calculating…" : "—";
    [JsonIgnore] public string DeckTransferSummary => TransferTotalBytes is > 0
        ? $"{Size(TransferredBytes)} / {Size(TransferTotalBytes.Value)}"
        : TransferredBytes > 0 ? Size(TransferredBytes) + " downloaded" : SizeSummary;
    [JsonIgnore] public string DeckQueueSize => TransferTotalBytes is > 0 ? Size(TransferTotalBytes.Value)
        : InstallationBytes > 0 ? "~" + Size(InstallationBytes) : TotalSize;
    [JsonIgnore] public string DeckRemaining => IsPaused ? "Paused" : !IsActive ? "—"
        : EtaSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 && seconds < 3_600_000
            ? $"About {(int)(seconds / 60)}:{(int)(seconds % 60):00} remaining"
            : string.IsNullOrWhiteSpace(Eta) ? "Calculating remaining time…" : $"About {Eta} remaining";
}
