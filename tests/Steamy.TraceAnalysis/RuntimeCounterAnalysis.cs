using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

internal static class RuntimeCounterAnalysis
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "cpu-usage", "working-set", "gc-heap-size", "gc-committed", "gc-fragmentation", "alloc-rate",
        "gen-0-gc-count", "gen-1-gc-count", "gen-2-gc-count", "time-in-gc", "gen-0-size", "gen-1-size", "gen-2-size",
        "loh-size", "poh-size", "threadpool-thread-count", "threadpool-queue-length", "active-timer-count",
        "monitor-lock-contention-count", "exception-count"
    };
    private sealed record Sample(double Milliseconds, string Name, string Units, double Value, bool Increment, double IntervalSeconds);

    internal static void Write(string input, string output)
    {
        if (File.Exists(output)) throw new IOException("Counter report already exists.");
        var samples = new List<Sample>();
        var runtimeEvents = 0;
        using var source = new EventPipeEventSource(Path.GetFullPath(input));
        source.Dynamic.All += data =>
        {
            if (data.ProviderName != "System.Runtime") return;
            runtimeEvents++;
            if (data.EventName != "EventCounters" || data.PayloadValue(0) is not IDictionary<string, object> fields) return;
            if (fields.TryGetValue("Payload", out var nested) && nested is IDictionary<string, object> payload) fields = payload;
            if (!fields.TryGetValue("Name", out var name) || name is not string counter || !Allowed.Contains(counter)) return;
            var mean = Number(fields.GetValueOrDefault("Mean"));
            var increment = Number(fields.GetValueOrDefault("Increment"));
            var interval = Number(fields.GetValueOrDefault("IntervalSec")) ?? 0;
            if (mean is null && increment is null) return;
            var units = fields.GetValueOrDefault("DisplayUnits") as string ?? "";
            samples.Add(new(data.TimeStampRelativeMSec, counter, units, mean ?? increment!.Value, mean is null, interval));
        };
        source.Process();
        var summary = samples.GroupBy(sample => sample.Name).Select(group =>
        {
            var values = group.Select(sample => sample.Value).Order().ToArray();
            var middle = values.Length / 2;
            var median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
            return new { name = group.Key, units = group.First().Units, increment = group.First().Increment,
                count = values.Length, minimum = values[0], median, maximum = values[^1] };
        }).OrderBy(item => item.name).ToArray();
        var report = new
        {
            input = Path.GetFullPath(input), source.EventsLost, runtimeEvents,
            scope = "Numeric System.Runtime counters only, attached to an existing user-started process. No app launch, UI operation or forced GC. Diagnostic overhead and unknown user activity prevent performance acceptance. Counter heap metrics exclude native WPF/WIC/render surfaces.",
            summary, samples
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { runtimeEvents, samples = samples.Count, source.EventsLost, summary }));
    }

    private static double? Number(object? value) => value is IConvertible convertible
        ? Convert.ToDouble(convertible, CultureInfo.InvariantCulture) : null;
    private static object? GetValueOrDefault(this IDictionary<string, object> fields, string name) => fields.TryGetValue(name, out var value) ? value : null;
}
