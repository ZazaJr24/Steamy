using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Steamy.Services;

public sealed record CachedDepotManifest(int DepotId, string ManifestId);
public sealed record DepotResumeState(int AppId, string TargetFolder, IReadOnlyList<CachedDepotManifest> Depots);

/// <summary>
/// Pins one download's exact manifest set independently of the source cache. The state file
/// contains identifiers only; depot keys stay in the local tool's existing key-file format.
/// </summary>
public static class DepotResumeStateStore
{
    private const string StateFileName = "resume.json";
    private const long MaximumStateBytes = 2 * 1024 * 1024;
    private const int MaximumDepots = 4096;

    public static string SessionDirectory(string root, int appId, string targetFolder)
    {
        var canonical = CanonicalTarget(targetFolder);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        return Path.Combine(root, "resume", appId.ToString(CultureInfo.InvariantCulture) + "-" + hash);
    }

    public static DepotResumeState? Read(string directory, int appId, string targetFolder)
    {
        try
        {
            var path = Path.Combine(directory, StateFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumStateBytes) return null;
            var state = JsonSerializer.Deserialize<DepotResumeState>(File.ReadAllText(path));
            if (state is null || state.AppId != appId || !IsValid(state)
                || CanonicalTarget(state.TargetFolder) != CanonicalTarget(targetFolder)) return null;
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(string directory, DepotResumeState state, CancellationToken cancellationToken)
    {
        if (!IsValid(state)) throw new ArgumentException("A resume session requires a valid app, target and unique depot manifests.", nameof(state));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, StateFileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsValid(DepotResumeState state) =>
        state.AppId > 0 && !string.IsNullOrWhiteSpace(state.TargetFolder)
        && Path.IsPathFullyQualified(state.TargetFolder)
        && state.Depots is { Count: > 0 and <= MaximumDepots }
        && state.Depots.All(depot => depot is not null && depot.DepotId > 0
            && IsManifestId(depot.ManifestId))
        && state.Depots.Select(depot => depot.DepotId).Distinct().Count() == state.Depots.Count;

    private static bool IsManifestId(string? value) => value is { Length: > 0 and <= 20 }
        && value.All(char.IsAsciiDigit)
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    private static string CanonicalTarget(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }
}
