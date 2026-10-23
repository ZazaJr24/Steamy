using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

// Isolated managed-allocation probe, not a real-app startup or process-RAM benchmark.
var portable = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var recommendations = args.Length > 2 && args[2] == "recommendations";
var searchIndex = args.Length > 2 && args[2] == "search";
var backdrop = args.Length > 2 && args[2] == "backdrop";
var queueRestore = args.Length > 2 && args[2] == "queue";
var profile = Path.Combine(Path.GetDirectoryName(output)!, "catalog-" + Guid.NewGuid().ToString("N"));
if (!searchIndex && !backdrop && !queueRestore) { Directory.CreateDirectory(profile); WriteFixture(profile); }
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(portable, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var dll = Path.Combine(portable, "Steamy.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
if (queueRestore)
{
    await QueueRestoreProbe.RunAsync(assembly, dll, output);
    return;
}
if (backdrop)
{
    await BackdropFrameProbe.RunAsync(assembly, dll, output);
    return;
}
if (searchIndex)
{
    await SearchIndexProbe.RunAsync(assembly, dll, output);
    return;
}
var type = assembly.GetType("Steamy.Services.SteamCatalogService")!;
using var service = (IDisposable)Activator.CreateInstance(type, [null, profile, null])!;
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var retainedBefore = GC.GetTotalMemory(true);
var allocatedBefore = GC.GetTotalAllocatedBytes(true);
var watch = Stopwatch.StartNew();
var method = recommendations ? type.GetMethod("GetRecommendationCandidatesAsync") : null;
var operation = (Task)(method is not null ? method.Invoke(service, [CancellationToken.None])!
    : type.GetMethod("GetCatalogAsync")!.Invoke(service, [false, CancellationToken.None])!);
await operation;
var result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
var items = (IEnumerable)(method is not null ? result : result.GetType().GetProperty("Items")!.GetValue(result)!);
var count = items.Cast<object>().Count();
watch.Stop();
var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
var retained = GC.GetTotalMemory(true) - retainedBefore;
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    scope = "Separate diagnostic process; 20,000 public metadata fixtures. Managed allocations and forced-GC retention only; not Steamy.exe RAM or UI latency.",
    mode = recommendations ? "recommendations" : "catalog", method = method?.Name ?? "GetCatalogAsync",
    count, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = allocated, retainedBytes = retained,
    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)))
}, new JsonSerializerOptions { WriteIndented = true }));
GC.KeepAlive(items); GC.KeepAlive(service);

[MethodImpl(MethodImplOptions.NoInlining)]
static void WriteFixture(string profile)
{
    // Keep fixture-construction temporaries out of the measured async state machine.
    File.WriteAllText(Path.Combine(profile, "app-list.json"), JsonSerializer.Serialize(new
    {
        FetchedAt = DateTimeOffset.UtcNow, ETag = (string?)null, LastModified = (string?)null,
        Items = Enumerable.Range(10000000, 20000).Select(id => new { AppId = id, Name = $"Fixture game {id}" }).ToArray()
    }));
}
