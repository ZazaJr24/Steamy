using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

internal static class QueueRestoreProbe
{
    internal static async Task RunAsync(Assembly assembly, string assemblyPath, string output)
    {
        var finished = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                _ = Measure(assembly, 20, dispatcher); // warm shared JIT/reflection paths, excluded
                var samples = Enumerable.Range(0, 5).Select(_ => Measure(assembly, 1000, dispatcher)).ToArray();
                finished.TrySetResult(new
                {
                    scope = "Separate STA diagnostic console. Five warm in-memory restores of 1000 synthetic downloads with the real shell queue observer. Database fixture setup excluded; publication runs on a pumped dispatcher. No Application, Window, production startup, accounts, network or helper process.",
                    samples, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                    applicationCreated = false, windowCreated = false, portableLaunched = false,
                    realStartupOrRamMeasurement = false
                });
            }
            catch (Exception error) { finished.TrySetException(error); }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var report = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Measure(Assembly assembly, int count, Dispatcher dispatcher)
    {
        Type Type(string name) => assembly.GetType("Steamy." + name, throwOnError: true)!;
        var store = Activator.CreateInstance(Type("Services.AppDataStore"))!;
        var jobs = (IList)store.GetType().GetProperty("Downloads")!.GetValue(store)!;
        var database = Activator.CreateInstance(Type("Services.NullLocalDatabase"))!;
        var jobType = Type("Models.DownloadJob"); var state = jobType.GetProperty("State")!;
        var name = jobType.GetProperty("GameName")!;
        var downloading = Enum.Parse(state.PropertyType, "Downloading");
        var queueType = Type("Services.DownloadQueueStore"); var row = queueType.GetMethod("ToRow")!;
        var save = database.GetType().GetMethod("SaveDownloadJobAsync")!;
        for (var i = 0; i < count; i++)
        {
            var job = Activator.CreateInstance(jobType)!;
            state.SetValue(job, downloading); name.SetValue(job, "Synthetic download " + i);
            ((Task)save.Invoke(database, [row.Invoke(null, [job]), CancellationToken.None])!).GetAwaiter().GetResult();
        }
        var log = DispatchProxy.Create(Type("Services.ILoggingService"), typeof(QueueProbeProxy));
        var artwork = DispatchProxy.Create(Type("Services.IArtworkService"), typeof(QueueProbeProxy));
        using var shell = (IDisposable)Activator.CreateInstance(Type("ViewModels.ShellDownloadViewModel"), [store, artwork])!;
        var queue = Activator.CreateInstance(queueType, [database, log])!;
        var notifications = 0;
        ((INotifyCollectionChanged)jobs).CollectionChanged += (_, _) => notifications++;
        var allocated = GC.GetTotalAllocatedBytes(false); var watch = Stopwatch.StartNew();
        var loading = (Task)queueType.GetMethod("RestoreAsync")!.Invoke(queue, [jobs, false, CancellationToken.None])!;
        var frame = new DispatcherFrame();
        _ = loading.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); loading.GetAwaiter().GetResult(); watch.Stop();
        if (jobs.Count != count) throw new InvalidOperationException("The synthetic queue was not restored.");
        return new { count, notifications, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated };
    }
}

public class QueueProbeProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method?.Name == "Add" && method.ReturnType == typeof(void)) return null;
        throw new InvalidOperationException("Unexpected operation in queue probe: " + method?.Name);
    }
}
