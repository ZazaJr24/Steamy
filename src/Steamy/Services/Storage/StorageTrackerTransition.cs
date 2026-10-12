using System.IO;
using System.Security.Cryptography;
using System.Text;
using Steamy.Playtime;
using Steamy.Storage;

namespace Steamy.Services;

internal static class StorageTrackerTransition
{
    public static void Prepare()
    {
        var state = AppDataPaths.Locations.Read();
        var roots = new[] { state.ActiveRoot, AppDataPaths.LegacyLocalRoot }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var saved = new Dictionary<string, byte[]>();
        try
        {
            foreach (var root in roots)
            {
                var configPath = Path.Combine(root, PlayTimeFiles.ConfigurationFile);
                if (!File.Exists(configPath)) continue;
                saved[root] = File.ReadAllBytes(configPath);
                if (!Running(root)) continue;
                var config = PlayTimeFiles.ReadAsync<TrackerConfiguration>(configPath).GetAwaiter().GetResult()
                    ?? throw new IOException("The running tracker configuration could not be read.");
                PlayTimeFiles.WriteAsync(configPath, config with { Enabled = false }).GetAwaiter().GetResult();
                for (var attempt = 0; attempt < 48 && Running(root); attempt++) Thread.Sleep(250);
                if (Running(root)) throw new IOException("The playtime tracker is still saving. Try starting Steamy again in a moment.");
            }
            AppDataPaths.Initialize();
            // Restore the chosen configuration in the new root; the old worker has already flushed.
            var original = saved.GetValueOrDefault(state.ActiveRoot) ?? saved.GetValueOrDefault(AppDataPaths.LegacyLocalRoot);
            if (original is not null) File.WriteAllBytes(Path.Combine(AppDataPaths.Root, PlayTimeFiles.ConfigurationFile), original);
        }
        catch
        {
            foreach (var entry in saved) File.WriteAllBytes(Path.Combine(entry.Key, PlayTimeFiles.ConfigurationFile), entry.Value);
            throw;
        }
    }
    private static bool Running(string root)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        try
        {
            using var mutex = Mutex.OpenExisting(@"Local\Steamy.PlayTimeTracker." + id);
            try { if (!mutex.WaitOne(0)) return true; } catch (AbandonedMutexException) { }
            mutex.ReleaseMutex(); return false;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}
