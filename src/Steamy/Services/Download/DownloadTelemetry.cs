using System.Globalization;

namespace Steamy.Services;

public sealed record DownloadTelemetry(int DepotId, string Phase, long ContentBytes, long TotalBytes,
    long TransferredBytes, long? TransferTotalBytes, long ReusedBytes)
{
    public const string Prefix = "STEAMY_PROGRESS|";
    public static DownloadTelemetry? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length > 512) return null;
        var fields = line.Split('|');
        if (fields.Length != 9 || fields[1] != "1" || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var depot) || depot <= 0
            || fields[3] is not ("checking" or "downloading" or "finalizing")) return null;
        var values = new long[5];
        for (var i = 0; i < values.Length; i++)
            if (!long.TryParse(fields[i + 4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])) return null;
        if (values[0] < 0 || values[1] < 0 || values[0] > values[1] || values[2] < 0
            || values[3] < -1 || values[4] < 0 || values[4] > values[0]
            || values[3] >= 0 && values[2] > values[3]) return null;
        return new(depot, fields[3], values[0], values[1], values[2], values[3] < 0 ? null : values[3], values[4]);
    }
}
