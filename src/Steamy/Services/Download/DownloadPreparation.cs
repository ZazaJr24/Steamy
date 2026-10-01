using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Steamy.Services;

public enum ManifestSource
{
    Ryuu,
    Zaza,
    Hubcap,
    DepotBox,
    Sushi
}

public sealed record RyuuDepotInfo(int DepotId, string ManifestId, string DecryptionKey);
public sealed record RyuuGameDownloadResult(bool Succeeded, string Message);
public sealed record PreparedDepotVersion(string ManifestId, long? SizeBytes = null, string? BuildLabel = null,
    long? CompressedSizeBytes = null, string? BranchName = null)
{
    public string Label => string.IsNullOrWhiteSpace(BuildLabel) ? $"Manifest {ManifestId}" : $"{BuildLabel} · Manifest {ManifestId}";
}

public sealed record PreparedDownloadDepot(int DepotId, string Name,
    IReadOnlyList<PreparedDepotVersion> Versions, string DefaultManifestId,
    string? ContentType = null, string? OperatingSystems = null, string? Languages = null,
    string? MetadataSource = null, string? SteamDbUrl = null, int? DlcAppId = null, int? SharedAppId = null);
public sealed record PreparedGameDownload(Guid Id, int AppId, ManifestSource Source,
    IReadOnlyList<PreparedDownloadDepot> Depots);
public sealed record GameDownloadPreparation(bool Succeeded, string Message, PreparedGameDownload? Plan = null);

/// <summary>Reads source-provided metadata without executing Lua or treating a manifest ID as a build ID.</summary>
public static class DownloadPreparationReader
{
    public const int MaximumLuaCharacters = 4 * 1024 * 1024;
    public const int MaximumEntries = 4096;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AddAppId = new(
        @"addappid\(\s*(\d+)(?:\s*,\s*\d+\s*,\s*""([a-fA-F0-9]{8,128})"")?[^)\r\n]*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, ParseTimeout);
    private static readonly Regex Manifest = new(
        @"setManifestid\(\s*(\d+)\s*,\s*""(\d+)""(?:\s*,\s*(\d+))?[^)\r\n]*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, ParseTimeout);
    private static readonly Regex ManifestFile = new(@"^(\d+)_(\d+)\.manifest$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, ParseTimeout);
    private static readonly Regex Build = new(@"^\s*--\s*(?:build\s*id|buildid)\s*[:=]\s*(\d{1,20})\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, ParseTimeout);
    private static readonly Regex Version = new(@"^\s*--\s*(?:game\s+version|version)\s*[:=]\s*([^\r\n]{1,100})\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, ParseTimeout);
    private static readonly Regex LuaComments = new(@"--\[(=*)\[.*?\]\1\]|--[^\r\n]*", RegexOptions.Singleline | RegexOptions.CultureInvariant, ParseTimeout);

    public sealed record Catalog(IReadOnlyList<PreparedDownloadDepot> Depots, IReadOnlyList<RyuuDepotInfo> Manifests);

    public static Catalog Read(string primaryLua, IEnumerable<string>? additionalLua = null,
        IEnumerable<string>? manifestFileNames = null, IReadOnlyDictionary<int, string>? fileKeys = null)
    {
        ArgumentNullException.ThrowIfNull(primaryLua);
        var options = new Dictionary<int, Dictionary<string, PreparedDepotVersion>>();
        var defaults = new Dictionary<int, string>();
        var keys = new Dictionary<int, string>();
        var count = 0;
        long luaCharacters = 0;
        var scripts = new[] { primaryLua }.Concat(additionalLua ?? []);
        var scriptIndex = 0;
        foreach (var script in scripts)
        {
            luaCharacters += script.Length;
            if (luaCharacters > MaximumLuaCharacters) throw new InvalidDataException("Lua metadata exceeds the parsing limit.");
            var build = Build.Match(script);
            var version = Version.Match(script);
            var buildLabel = build.Success ? $"Build {build.Groups[1].Value}" : version.Success ? $"Version {version.Groups[1].Value.Trim()}" : null;
            // Commented-out calls are not downloadable depots. Call arguments contain only
            // numbers and hex strings, so stripping Lua line comments cannot change them.
            var activeLua = LuaComments.Replace(script, "");
            foreach (Match match in AddAppId.Matches(activeLua))
            {
                if (++count > MaximumEntries) throw new InvalidDataException("Too many depot metadata entries.");
                if (TryDepot(match.Groups[1].Value, out var depotId) && match.Groups[2].Success)
                {
                    if (scriptIndex == 0) keys[depotId] = match.Groups[2].Value;
                    else keys.TryAdd(depotId, match.Groups[2].Value);
                }
            }
            foreach (Match match in Manifest.Matches(activeLua))
            {
                if (++count > MaximumEntries) throw new InvalidDataException("Too many depot metadata entries.");
                if (!TryDepot(match.Groups[1].Value, out var depotId) || !IsManifestId(match.Groups[2].Value)) continue;
                var manifestId = match.Groups[2].Value;
                var size = match.Groups[3].Success && long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : (long?)null;
                if (!options.TryGetValue(depotId, out var versions)) options[depotId] = versions = new(StringComparer.Ordinal);
                var prepared = new PreparedDepotVersion(manifestId, size, buildLabel);
                if (scriptIndex == 0) versions[manifestId] = prepared;
                else versions.TryAdd(manifestId, prepared);
                defaults.TryAdd(depotId, manifestId);
                if (scriptIndex == 0) defaults[depotId] = manifestId;
            }
            scriptIndex++;
        }
        if (fileKeys is not null)
        {
            if (fileKeys.Count > MaximumEntries) throw new InvalidDataException("Too many depot key entries.");
            foreach (var entry in fileKeys)
                if (entry.Key > 0 && entry.Value is { Length: >= 8 and <= 128 } key && key.All(Uri.IsHexDigit))
                    keys.TryAdd(entry.Key, key);
        }

        foreach (var name in manifestFileNames ?? [])
        {
            if (++count > MaximumEntries) throw new InvalidDataException("Too many depot metadata entries.");
            var match = ManifestFile.Match(Path.GetFileName(name));
            if (!match.Success || !TryDepot(match.Groups[1].Value, out var depotId) || !IsManifestId(match.Groups[2].Value)) continue;
            var manifestId = match.Groups[2].Value;
            if (!options.TryGetValue(depotId, out var versions)) options[depotId] = versions = new(StringComparer.Ordinal);
            versions.TryAdd(manifestId, new(manifestId));
            defaults.TryAdd(depotId, manifestId);
        }

        var depots = options.OrderBy(pair => pair.Key).Select(pair => new PreparedDownloadDepot(pair.Key,
            $"Depot {pair.Key}", Array.AsReadOnly(pair.Value.Values.OrderByDescending(value => value.ManifestId == defaults[pair.Key]).ThenBy(value => value.ManifestId, StringComparer.Ordinal).ToArray()), defaults[pair.Key])).ToArray();
        var manifests = depots.SelectMany(depot => depot.Versions.Select(version => new RyuuDepotInfo(depot.DepotId, version.ManifestId, keys.GetValueOrDefault(depot.DepotId, string.Empty)))).ToArray();
        return new(Array.AsReadOnly(depots), Array.AsReadOnly(manifests));
    }

    public static IReadOnlyList<RyuuDepotInfo> Select(Catalog catalog, IReadOnlyList<CachedDepotManifest> selections)
    {
        if (selections is not { Count: > 0 } || selections.Count > MaximumEntries
            || selections.Any(selection => selection is null)
            || selections.Select(selection => selection.DepotId).Distinct().Count() != selections.Count)
            throw new ArgumentException("Select at least one depot and one version per depot.", nameof(selections));
        return selections.Select(selection => catalog.Manifests.FirstOrDefault(manifest =>
                manifest.DepotId == selection.DepotId && manifest.ManifestId == selection.ManifestId)
            ?? throw new ArgumentException("A selected depot version is not in this source snapshot.", nameof(selections))).ToArray();
    }

    public static bool IsManifestId(string? value) => value is { Length: > 0 and <= 20 }
        && value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    public static bool IsSafePackageFileName(string? name) => name is { Length: > 0 and <= 255 }
        && name is not "." and not ".." && name.IndexOfAny(['/', '\\', ':', '\0']) < 0
        && Path.GetFileName(name) == name;

    private static bool TryDepot(string value, out int id) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
