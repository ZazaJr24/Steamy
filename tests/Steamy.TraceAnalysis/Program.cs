using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length == 3 && args[0] == "--runtime-counters")
{
    RuntimeCounterAnalysis.Write(args[1], args[2]);
    return;
}

if (args.Length is < 2 or > 5) throw new ArgumentException("Expected a nettrace path, output JSON path, optional minimum/maximum trace-relative milliseconds, and optional navigation frame filter.");
var minimumMilliseconds = args.Length >= 3 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
var maximumMilliseconds = args.Length >= 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : double.MaxValue;
var navigationFrameFilter = args.Length == 5 ? args[4] : null;
var tracePath = Path.GetFullPath(args[0]);
var etlx = Path.ChangeExtension(tracePath, ".etlx");
if (!File.Exists(etlx)) TraceLog.CreateFromEventPipeDataFile(tracePath, etlx);
using var trace = new TraceLog(etlx);
var types = new Dictionary<string, long>();
var applicationFrames = new Dictionary<string, long>();
var allocationStacks = new Dictionary<string, long>();
var navigationSamples = new Dictionary<string, int>();
var threadSamples = new Dictionary<int, Dictionary<string, int>>();
long sampledBytes = 0, navigationBytes = 0;
int allocationEvents = 0, navigationEvents = 0;
foreach (var data in trace.Events)
{
    if (data.TimeStampRelativeMSec < minimumMilliseconds || data.TimeStampRelativeMSec > maximumMilliseconds) continue;
    if (data is not GCAllocationTickTraceData && data.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
    var frames = new List<string>();
    for (var frame = data.CallStack(); frame is not null && frames.Count < 120; frame = frame.Caller)
        frames.Add(frame.CodeAddress.FullMethodName);
    var navigation = frames.Any(frame => frame.Contains("NavigationView.Navigate") || frame.Contains("NavigationViewPageProvider.GetPage"))
        && (navigationFrameFilter is null || frames.Any(frame => frame.Contains(navigationFrameFilter, StringComparison.Ordinal)));
    if (data is GCAllocationTickTraceData allocation)
    {
        var bytes = allocation.AllocationAmount64 > 0 ? allocation.AllocationAmount64 : allocation.AllocationAmount;
        sampledBytes += bytes; allocationEvents++;
        types[allocation.TypeName] = types.GetValueOrDefault(allocation.TypeName) + bytes;
        foreach (var frame in frames.Where(frame => frame.StartsWith("Steamy.", StringComparison.Ordinal)).Distinct())
            applicationFrames[frame] = applicationFrames.GetValueOrDefault(frame) + bytes;
        if (navigation)
        {
            navigationBytes += bytes; navigationEvents++;
            var stack = string.Join("\n", frames.Take(24));
            allocationStacks[stack] = allocationStacks.GetValueOrDefault(stack) + bytes;
        }
    }
    else
    {
        if (!threadSamples.TryGetValue(data.ThreadID, out var counts)) threadSamples[data.ThreadID] = counts = new();
        foreach (var frame in frames.Distinct()) counts[frame] = counts.GetValueOrDefault(frame) + 1;
        if (navigation)
            foreach (var frame in frames.Distinct()) navigationSamples[frame] = navigationSamples.GetValueOrDefault(frame) + 1;
    }
}
var result = new
{
    tracePath, minimumMilliseconds, maximumMilliseconds, navigationFrameFilter, trace.EventsLost, allocationEvents, sampledBytes, navigationEvents, navigationBytes,
    scope = "GC allocation ticks are sampled, weighted estimates, not an exact object census. Inclusive frame weights overlap. Thread samples indicate sampled wall-clock stacks, not CPU utilization. Navigation scope includes only stacks containing NavigationView.Navigate or NavigationViewPageProvider.GetPage; deferred layout and Loaded work may be outside it. Profiling overhead invalidates performance-target comparisons.",
    types = types.OrderByDescending(pair => pair.Value).Take(30).Select(pair => new { type = pair.Key, estimatedBytes = pair.Value }),
    applicationFrames = applicationFrames.OrderByDescending(pair => pair.Value).Take(40).Select(pair => new { frame = pair.Key, inclusiveEstimatedBytes = pair.Value }),
    resourceUpdatePaths = applicationFrames.Where(pair => pair.Key.Contains("UpdateLibraryAmbient") || pair.Key.Contains("UpdateDashboardBodyColors") || pair.Key.Contains("UpdateDownloadScene") || pair.Key.Contains("ApplyDeckAccessibility"))
        .OrderByDescending(pair => pair.Value).Select(pair => new { frame = pair.Key, inclusiveEstimatedBytes = pair.Value }),
    navigationAllocationStacks = allocationStacks.OrderByDescending(pair => pair.Value).Take(20).Select(pair => new { stack = pair.Key, estimatedBytes = pair.Value }),
    navigationThreadSamples = navigationSamples.OrderByDescending(pair => pair.Value).Take(40).Select(pair => new { frame = pair.Key, inclusiveSamples = pair.Value }),
    threadSamples = threadSamples.Select(thread => new { threadId = thread.Key,
        frames = thread.Value.OrderByDescending(pair => pair.Value).Take(35).Select(pair => new { frame = pair.Key, inclusiveSamples = pair.Value }) })
};
File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{allocationEvents} allocation ticks, {navigationEvents} during synchronous navigation; estimated {navigationBytes / 1048576d:F1} MiB, lost events {trace.EventsLost}.");
