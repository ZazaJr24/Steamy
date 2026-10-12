using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Steamy.Playtime;
using Steamy.Tracking;

var data = PlayTimeFiles.DefaultDirectory;
if (args.Length == 2 && args[0] == "--data-dir" && Path.IsPathFullyQualified(args[1])) data = Path.GetFullPath(args[1]);
var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..24];
// Keep ownership on this thread; awaiting while holding a Mutex can release it on another thread.
using var singleton = new Mutex(true, @"Local\Steamy.PlayTimeTracker." + identity, out var created);
if (!created) return;
try { RunAsync(data).GetAwaiter().GetResult(); }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
{
    try { File.WriteAllText(Path.Combine(data, "playtime-error.log"), $"{DateTimeOffset.Now:u} {e.GetType().Name}: {e.Message}"); } catch { }
}
finally { singleton.ReleaseMutex(); }

static async Task RunAsync(string data)
{
    var configPath = Path.Combine(data, PlayTimeFiles.ConfigurationFile);
    var timePath = Path.Combine(data, PlayTimeFiles.TimeFile);
    var saved = await PlayTimeFiles.ReadAsync<PlayTimeSnapshot>(timePath);
    var accumulator = new PlayTimeAccumulator(saved);
    IReadOnlyList<TrackedGame> games = [];
    var refresh = DateTimeOffset.MinValue;
    var save = DateTimeOffset.MinValue;
    using var ownProcess = Process.GetCurrentProcess();
    var ownSession = ownProcess.SessionId;
    while (true)
    {
        var config = await PlayTimeFiles.ReadAsync<TrackerConfiguration>(configPath);
        if (config is null || !config.Enabled) break;
        var now = DateTimeOffset.UtcNow;
        if (now >= refresh)
        {
            games = SteamGameReader.Read(config);
            refresh = now.AddSeconds(30);
        }
        var active = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != ownSession) continue;
                    using var handle = Native.OpenProcess(0x1000, false, process.Id);
                    if (handle.IsInvalid) continue;
                    var path = new StringBuilder(32768);
                    var size = path.Capacity;
                    if (Native.QueryFullProcessImageName(handle, 0, path, ref size)
                        && GameProcessMatcher.Match(path.ToString(), games) is { } id) active.Add(id);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        // Unbiased interrupt time excludes Windows sleep and hibernation.
        Native.QueryUnbiasedInterruptTime(out var awakeTicks);
        var changed = accumulator.Sample(active, TimeSpan.FromTicks((long)awakeTicks));
        if (changed || now >= save)
        {
            await PlayTimeFiles.WriteAsync(timePath, accumulator.Snapshot(now));
            save = now.AddSeconds(15);
        }
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    var stopped = accumulator.Snapshot(DateTimeOffset.UtcNow);
    await PlayTimeFiles.WriteAsync(timePath, stopped with { Games = stopped.Games.Select(g => g with { IsRunning = false }).ToArray() });
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle handle, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryUnbiasedInterruptTime(out ulong time);
}
