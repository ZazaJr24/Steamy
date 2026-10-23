using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SearchIndexProbe
{
    public sealed record App(int Id, string Name);

    internal static async Task RunAsync(Assembly assembly, string assemblyPath, string output)
    {
        var type = assembly.GetType("Steamy.Services.GameSearchIndex`1", throwOnError: true)!.MakeGenericType(typeof(App));
        var constructor = type.GetConstructors().Single(item => item.GetParameters().Length == 3);
        var find = type.GetMethod("Find")!;
        Func<App, int> id = static app => app.Id;
        Func<App, string> name = static app => app.Name;
        object Create(App[] items) => constructor.Invoke([items, id, name]);
        object Find(object index, string query) => find.Invoke(index, [query, CancellationToken.None, null])!;
        int[] Ids(object result) => ((IEnumerable)result.GetType().GetProperty("Items")!.GetValue(result)!).Cast<App>().Select(app => app.Id).ToArray();
        var apps = Enumerable.Range(1, 20_000).Select(number => new App(number, $"Offline Library fixture{number:D5}")).ToArray();
        _ = Find(Create([new(1, "Warmup")]), "warmup");
        var retainedBefore = GC.GetTotalMemory(true);
        var (index, construction) = Measure(() => Create(apps));
        var retainedAfterConstruction = GC.GetTotalMemory(true) - retainedBefore;
        var (exactResult, exact) = Measure(() => Find(index, "offline fixture12345"));
        var (typoResult, typo) = Measure(() => Find(index, "offlien fixture00999"));
        var retainedAfterTypo = GC.GetTotalMemory(true) - retainedBefore;
        var (_, repeatedExact) = Measure(() =>
        {
            object? result = null;
            for (var iteration = 0; iteration < 10; iteration++) result = Find(index, "offline fixture12345");
            return result!;
        });
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            scope = "Separate diagnostic console process, 20,000 synthetic titles; no Application, Window or Steamy.exe startup. Forced GC belongs only to this probe, never the user's process.",
            count = apps.Length,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
            construction, exact, typo, repeatedExact,
            retainedAfterConstruction, retainedAfterTypo,
            exactIds = Ids(exactResult), typoIds = Ids(typoResult),
            isTypoMatch = (bool)typoResult.GetType().GetProperty("IsTypoMatch")!.GetValue(typoResult)!,
            realAppRamMeasurement = false,
            portableLaunched = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(index); GC.KeepAlive(apps);
    }

    private static (object Value, Phase Metrics) Measure(Func<object> operation)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        var value = operation();
        clock.Stop();
        return (value, new(GC.GetTotalAllocatedBytes(true) - before, clock.Elapsed.TotalMilliseconds));
    }
    private sealed record Phase(long AllocatedBytes, double ElapsedMs);
}
