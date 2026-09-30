using System.Collections.ObjectModel;
using System.Globalization;
using Steamy.Models;

namespace Steamy.Services;

public static class DownloadPresentation
{
    public static bool Matches(string name, int appId, DownloadJobState state, string search, string filter)
    {
        var query = search.Trim();
        if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)
            && !appId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)) return false;
        return filter switch
        {
            "Active" => state is DownloadJobState.Downloading or DownloadJobState.Preparing or DownloadJobState.Verifying,
            "Queued" => state == DownloadJobState.Queued,
            "Paused" => state == DownloadJobState.Paused,
            "Completed" => state == DownloadJobState.Completed,
            "Failed" => state == DownloadJobState.Failed,
            "Cancelled" => state == DownloadJobState.Cancelled,
            _ => true
        };
    }

    // A partial estimate must not promise that all active jobs finish at that time.
    public static double? RemainingSeconds(IEnumerable<double?> estimates)
    {
        double? longest = null;
        foreach (var estimate in estimates)
        {
            if (estimate is null || !double.IsFinite(estimate.Value) || estimate < 0) return null;
            longest = Math.Max(longest ?? 0, estimate.Value);
        }
        return longest;
    }

    public static int StateOrder(DownloadJobState state) => state switch
    {
        DownloadJobState.Downloading or DownloadJobState.Preparing or DownloadJobState.Verifying => 0,
        DownloadJobState.Paused => 1,
        DownloadJobState.Queued => 2,
        DownloadJobState.Failed => 3,
        DownloadJobState.Completed => 4,
        _ => 5
    };

    // Preserve row identity, selection and expanded details when only a few jobs change.
    public static void Synchronize<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : notnull
    {
        var keep = desired.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
            if (!keep.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], desired[index])) continue;
            var current = target.IndexOf(desired[index]);
            if (current >= 0) target.Move(current, index);
            else target.Insert(index, desired[index]);
        }
    }
}
