using System.IO;
using Steamy.Playtime;

namespace Steamy.Services;

public sealed record SteamPlayTimes(string Account, IReadOnlyDictionary<int, long> Seconds)
{
    public IReadOnlyDictionary<int, DateTimeOffset> LastPlayed { get; init; } = new Dictionary<int, DateTimeOffset>();
}

public static class SteamPlayTimeReader
{
    public static SteamPlayTimes? Read(string steamRoot)
    {
        try
        {
            if (!Directory.Exists(steamRoot)) return null;
            var users = VdfParser.Parse(ReadFile(Path.Combine(steamRoot, "config", "loginusers.vdf")))["users"];
            var account = users?.Children.OrderByDescending(u => u.GetInt("MostRecent")).ThenByDescending(u => u.GetLong("Timestamp"))
                .Select(u => ulong.TryParse(u.Key, out var id) ? ((uint)(id & uint.MaxValue)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
                .FirstOrDefault(id => id.Length > 0);
            var userRoot = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userRoot)) return null;
            var config = account is not null ? Path.Combine(userRoot, account, "config", "localconfig.vdf")
                : Directory.EnumerateDirectories(userRoot).Where(d => uint.TryParse(Path.GetFileName(d), out var id) && id > 0)
                    .Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (config is null || !File.Exists(config)) return null;
            account ??= Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config))!);
            var root = VdfParser.Parse(ReadFile(config));
            var apps = root["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"];
            if (apps is null) return null;
            var times = new Dictionary<int, long>();
            var lastPlayed = new Dictionary<int, DateTimeOffset>();
            foreach (var app in apps.Children)
                if (int.TryParse(app.Key, out var id) && id > 0)
                {
                    if (app.GetLong("Playtime", -1) is var minutes && minutes >= 0 && minutes < 5_259_600) times[id] = minutes * 60;
                    if (app.GetLong("LastPlayed", -1) is var timestamp && timestamp > 0 && timestamp <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
                        lastPlayed[id] = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                }
            return new(account, times) { LastPlayed = lastPlayed };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static string ReadFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record PlayTimeImport(int AppId, long SteamSeconds, long LocalSeconds);
public sealed record PlayTimeImportState(string Account, IReadOnlyList<PlayTimeImport> Games);

public static class SteamPlayTimeMerger
{
    // Steam and the collector can observe the same session. Never sum their overlapping totals.
    public static (PlayTimeSnapshot Snapshot, PlayTimeImportState State) Merge(PlayTimeSnapshot local, SteamPlayTimes steam, PlayTimeImportState? state)
    {
        var baselines = state?.Account == steam.Account && state.Games is not null
            ? state.Games.Where(g => g.AppId > 0 && g.SteamSeconds is >= 0 and < 315_576_000 && g.LocalSeconds is >= 0 and < 315_576_000)
                .DistinctBy(g => g.AppId).ToDictionary(g => g.AppId) : new Dictionary<int, PlayTimeImport>();
        var localGames = local.Games.DistinctBy(g => g.AppId).ToDictionary(g => g.AppId);
        var result = new List<GamePlayTime>();
        foreach (var id in localGames.Keys.Concat(steam.Seconds.Keys).Concat(baselines.Keys).Distinct())
        {
            var observed = localGames.GetValueOrDefault(id);
            var seconds = Math.Clamp(observed?.Seconds ?? 0, 0, 315_576_000);
            if (steam.Seconds.TryGetValue(id, out var steamSeconds) && !baselines.ContainsKey(id))
                baselines[id] = new(id, Math.Max(steamSeconds, seconds), seconds);
            if (baselines.TryGetValue(id, out var baseline))
                seconds = Math.Max(steam.Seconds.GetValueOrDefault(id), baseline.SteamSeconds + Math.Max(0, seconds - baseline.LocalSeconds));
            result.Add(new(id, seconds, observed?.IsRunning == true));
        }
        return (new(1, local.UpdatedAt, result), new(steam.Account, baselines.Values.OrderBy(g => g.AppId).ToArray()));
    }
}
