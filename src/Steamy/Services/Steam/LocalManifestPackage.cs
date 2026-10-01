using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>Imports metadata into an owned snapshot. Lua is parsed as text and is never executed.</summary>
public static class LocalManifestPackage
{
    public const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly Regex AppCall = new(@"\baddappid\s*\(\s*(\d+)(?:\s*,\s*\d+)?\s*\)", RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex Comments = new(@"--\[(=*)\[.*?\]\1\]|--[^\r\n]*", RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(250));
    public sealed record Import(string Lua, string Directory);

    public static bool SafeSegment(string name) => DownloadPreparationReader.IsSafePackageFileName(name)
        && !name.Any(ch => char.IsControl(ch) || "<>\"|?*".Contains(ch))
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && !Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase);

    public static async Task<Import> ImportAsync(string path, int appId, string directory, CancellationToken token = default)
    {
        if (appId <= 0) throw new InvalidDataException("Choose a valid game before importing a package.");
        if (Directory.Exists(directory)) throw new IOException("The import folder must be new.");
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string lua;
            if (Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > DownloadPreparationReader.MaximumLuaCharacters)
                    throw new InvalidDataException("The Lua metadata is too large.");
                lua = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), token).ConfigureAwait(false);
                ValidateApp(lua, appId);
            }
            else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                lua = await ReadZipAsync(path, appId, directory, token).ConfigureAwait(false);
            else throw new InvalidDataException("Select a ZIP or Lua metadata package.");

            if (DownloadPreparationReader.Read(lua, manifestFileNames: Directory.EnumerateFiles(directory, "*.manifest")).Depots.Count == 0)
                throw new InvalidDataException("This package contains no usable depot manifests for the selected game.");
            await File.WriteAllTextAsync(Path.Combine(directory, $"{appId}.lua"), lua, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(lua, directory);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    private static void ValidateApp(string lua, int appId)
    {
        var active = Comments.Replace(lua, "");
        var calls = Regex.Matches(active, @"\bsetManifestid\s*\(([^)]*)\)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (calls.Count != Regex.Matches(active, @"\bsetManifestid\s*\(", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)).Count)
            throw new InvalidDataException("The Lua contains incomplete manifest metadata.");
        foreach (Match call in calls)
        {
            var parsed = Regex.Match(call.Groups[1].Value, "^\\s*(\\d+)\\s*,\\s*\"(\\d+)\"(?:\\s*,\\s*\\d+)?\\s*$");
            if (!parsed.Success || !int.TryParse(parsed.Groups[1].Value, out var depot) || depot <= 0
                || !DownloadPreparationReader.IsManifestId(parsed.Groups[2].Value))
                throw new InvalidDataException("The Lua contains damaged depot or manifest metadata.");
        }
        var identities = AppCall.Matches(Comments.Replace(lua, "")).Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (identities.Length != 1) throw new InvalidDataException("The Lua package has missing or ambiguous game metadata. Use a package with one explicit addappid(gameId) call.");
        if (identities[0] != appId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"This package belongs to App {identities[0]}, not the selected App {appId}.");
    }

    private static async Task<string> ReadZipAsync(string path, int appId, string directory, CancellationToken token)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The ZIP exceeds the 512 MB import limit.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("The ZIP contains too many entries.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.Contains('\\') || name.Length > 1024 || !names.Add(name)
                || name.TrimEnd('/').Split('/').Any(part => !SafeSegment(part))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The ZIP contains an unsafe, linked or duplicate filename.");
            if (entry.Length < 0 || entry.Length > MaximumBytes - total)
                throw new InvalidDataException("The expanded ZIP exceeds the 512 MB import limit.");
            total += entry.Length;
        }

        var metadata = new List<(ZipArchiveEntry Entry, JsonDocument Document)>();
        try
        {
            foreach (var entry in zip.Entries.Where(entry => entry.Name.Equals("steamy.json", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = await ReadAsync(entry, DownloadPreparationReader.MaximumLuaCharacters, token).ConfigureAwait(false);
                var document = JsonDocument.Parse(bytes);
                metadata.Add((entry, document));
                if (document.RootElement.GetProperty("format").GetString() != "steamy-share/1")
                    throw new InvalidDataException("Unsupported Steamy package metadata.");
            }
            if (metadata.Count > 0)
            {
                var selected = metadata.Where(item => item.Document.RootElement.GetProperty("appId").GetInt32() == appId).ToArray();
                if (selected.Length != 1) throw new InvalidDataException(selected.Length == 0
                    ? "This bundle does not contain the selected game." : "This bundle contains ambiguous copies of the selected game.");
                var chosen = selected[0];
                var prefix = chosen.Entry.FullName[..^chosen.Entry.Name.Length];
                var root = chosen.Document.RootElement;
                var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var scripts = new List<string>();
                foreach (var file in root.GetProperty("files").EnumerateArray())
                {
                    var name = file.GetProperty("name").GetString() ?? "";
                    if (!SafeSegment(name) || !listedNames.Add(name)) throw new InvalidDataException("Invalid or duplicate package metadata filename.");
                    var entry = zip.Entries.SingleOrDefault(entry => entry.FullName == prefix + name)
                        ?? throw new InvalidDataException($"The package is missing {name}.");
                    var bytes = await ReadAsync(entry, Path.GetExtension(name).Equals(".manifest", StringComparison.OrdinalIgnoreCase)
                        ? MaximumBytes : DownloadPreparationReader.MaximumLuaCharacters, token).ConfigureAwait(false);
                    var expected = file.GetProperty("sha256").GetString();
                    if (file.GetProperty("bytes").GetInt64() != bytes.LongLength
                        || !string.Equals(expected, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Hash verification failed for {name}. The package is damaged or changed.");
                    var extension = Path.GetExtension(name).ToLowerInvariant();
                    if (extension == ".lua") scripts.Add(new UTF8Encoding(false, true).GetString(bytes));
                    else if (extension is ".key" or ".manifest")
                        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, token).ConfigureAwait(false);
                }
                if (zip.Entries.Any(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                    && Path.GetExtension(entry.Name).ToLowerInvariant() is ".lua" or ".key" or ".manifest"
                    && !listedNames.Contains(entry.Name)))
                    throw new InvalidDataException("The package contains unlisted metadata files.");
                if (scripts.Count > 0)
                {
                    foreach (var script in scripts) ValidateApp(script, appId);
                    return string.Join("\n", scripts);
                }
                // Steam library exports can contain ACF + manifests without a Lua script.
                var lua = new StringBuilder($"addappid({appId})\n");
                foreach (var depot in root.GetProperty("depots").EnumerateArray())
                {
                    var id = depot.GetProperty("depot").GetInt32();
                    var manifest = depot.GetProperty("manifest").GetString();
                    if (id <= 0 || !DownloadPreparationReader.IsManifestId(manifest))
                        throw new InvalidDataException("The package depot metadata is incomplete or damaged.");
                    lua.AppendLine($"setManifestid({id}, \"{manifest}\")");
                }
                return lua.ToString();
            }

            var luaEntries = zip.Entries.Where(entry => entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToArray();
            var primary = luaEntries.Where(entry => entry.Name.Equals($"{appId}.lua", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (primary.Length == 0 && luaEntries.Length == 1) primary = luaEntries;
            if (primary.Length != 1) throw new InvalidDataException("No unambiguous Lua metadata for the selected game was found in the ZIP.");
            var primaryLua = new UTF8Encoding(false, true).GetString(await ReadAsync(primary[0], DownloadPreparationReader.MaximumLuaCharacters, token).ConfigureAwait(false));
            ValidateApp(primaryLua, appId);
            var catalog = DownloadPreparationReader.Read(primaryLua);
            var depotIds = catalog.Depots.Select(depot => depot.DepotId.ToString(CultureInfo.InvariantCulture)).ToHashSet();
            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folder = primary[0].FullName[..^primary[0].Name.Length];
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(folder, StringComparison.Ordinal)
                && Path.GetExtension(entry.Name).ToLowerInvariant() is ".key" or ".manifest"))
            {
                if (Path.GetExtension(entry.Name).Equals(".manifest", StringComparison.OrdinalIgnoreCase)
                    && !depotIds.Contains(entry.Name.Split('_')[0])) continue;
                if (!copied.Add(entry.Name)) throw new InvalidDataException("The package has ambiguous depot filenames.");
                var bytes = await ReadAsync(entry, entry.Name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
                    ? DownloadPreparationReader.MaximumLuaCharacters : MaximumBytes, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(directory, entry.Name), bytes, token).ConfigureAwait(false);
            }
            return primaryLua;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or DecoderFallbackException)
        { throw new InvalidDataException("The package metadata is damaged or incomplete.", exception); }
        finally { foreach (var item in metadata) item.Document.Dispose(); }
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, long limit, CancellationToken token)
    {
        if (entry.Length > limit) throw new InvalidDataException($"{entry.Name} exceeds the metadata size limit.");
        await using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Expanded metadata exceeds the import limit.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) throw new InvalidDataException("The ZIP contains a truncated entry.");
        return output.ToArray();
    }
}
