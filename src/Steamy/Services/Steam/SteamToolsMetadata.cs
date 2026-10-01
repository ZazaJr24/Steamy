using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

public sealed record SteamToolsMetadataPlan(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<int> AppIds, int ManifestCount);

/// <summary>Reads configuration only; imported Lua is never evaluated.</summary>
public static class SteamToolsMetadata
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Comments = new(@"--\[(=*)\[.*?\]\1\]|--[^\r\n]*", RegexOptions.Singleline, Timeout);
    private static readonly Regex Calls = new("\\b(addappid|setManifestid)\\s*\\(([^)]*)\\)", RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex Add = new("^\\s*(\\d+)\\s*(?:,\\s*(\\d+)\\s*(?:,\\s*[\"']([a-fA-F0-9]{8,128})[\"']\\s*)?)?$", RegexOptions.None, Timeout);
    private static readonly Regex Manifest = new("^\\s*(\\d+)\\s*,\\s*[\"'](\\d+)[\"']\\s*(?:,\\s*(\\d+)\\s*)?$", RegexOptions.None, Timeout);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static (int AppId, string Lua) ReadLua(string content, string fileName, int? requestedAppId = null)
    {
        if (content.Length > DownloadPreparationReader.MaximumLuaCharacters) throw new InvalidDataException("Lua metadata is too large.");
        var active = Comments.Replace(content.TrimStart('\uFEFF'), "");
        var calls = Calls.Matches(active);
        if (calls.Count is 0 or > DownloadPreparationReader.MaximumEntries || Calls.Replace(active, "").Any(ch => !char.IsWhiteSpace(ch) && ch != ';'))
            throw new InvalidDataException("The file contains unsupported Lua code. Select addappid/setManifestid metadata.");
        var ids = new HashSet<int>();
        var output = new StringBuilder();
        foreach (Match call in calls)
        {
            var isAdd = call.Groups[1].Value.Equals("addappid", StringComparison.OrdinalIgnoreCase);
            var args = (isAdd ? Add : Manifest).Match(call.Groups[2].Value);
            if (!args.Success || !int.TryParse(args.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidDataException("The Lua metadata has an invalid app or depot ID.");
            if (isAdd)
            {
                if (!args.Groups[3].Success) ids.Add(id);
                var flag = args.Groups[2].Success ? args.Groups[2].Value : null;
                if (flag is not null && !uint.TryParse(flag, out _)) throw new InvalidDataException("Invalid addappid flag.");
                output.AppendLine(flag is null ? $"addappid({id})" : args.Groups[3].Success
                    ? $"addappid({id},{flag},\"{args.Groups[3].Value}\")" : $"addappid({id},{flag})");
            }
            else
            {
                if (!DownloadPreparationReader.IsManifestId(args.Groups[2].Value)) throw new InvalidDataException("Invalid manifest ID.");
                if (args.Groups[3].Success && !ulong.TryParse(args.Groups[3].Value, out _)) throw new InvalidDataException("Invalid manifest size.");
                output.AppendLine($"setManifestid({id},\"{args.Groups[2].Value}\"{(args.Groups[3].Success ? "," + args.Groups[3].Value : "")})");
            }
        }
        var named = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"^(\d+)(?:\D|$)", RegexOptions.None, Timeout);
        int? fromName = named.Success && int.TryParse(named.Groups[1].Value, out var parsed) && ids.Contains(parsed) ? parsed : null;
        var appId = requestedAppId ?? fromName ?? (ids.Count == 1 ? ids.Single() : (int?)null);
        if (appId is null || !ids.Contains(appId.Value)) throw new InvalidDataException("The game ID is missing or ambiguous. Enter its Steam App ID.");
        return (appId.Value, output.ToString());
    }

    public static async Task<SteamToolsMetadataPlan> ReadAsync(IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default)
    {
        if (paths.Count is 0 or > DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("Select ZIP, Lua or manifest files.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        long bytes = 0;
        void Put(string target, byte[] data)
        {
            if (files.TryGetValue(target, out var previous))
            {
                if (!previous.AsSpan().SequenceEqual(data)) throw new InvalidDataException("The selection contains conflicting copies of " + target);
                return;
            }
            bytes += data.LongLength;
            if (bytes > LocalManifestPackage.MaximumBytes || files.Count >= DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("The metadata selection is too large.");
            files.Add(target, data);
        }
        void Lua(string name, byte[] data, int? requested)
        {
            var parsed = ReadLua(Utf8.GetString(data), name, requested);
            ids.Add(parsed.AppId);
            Put($"config/stplug-in/{parsed.AppId}.lua", Utf8.GetBytes(parsed.Lua));
        }
        void Depot(string name, byte[] data)
        {
            var match = Regex.Match(name, @"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase, Timeout);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var depot) || depot <= 0 || !DownloadPreparationReader.IsManifestId(match.Groups[2].Value) || data.Length == 0)
                throw new InvalidDataException("A manifest must be named depot_manifest.manifest and contain data.");
            Put("depotcache/" + name, data);
        }
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > LocalManifestPackage.MaximumBytes) throw new InvalidDataException("The selected file is too large.");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".lua")
            {
                if (new FileInfo(path).Length > DownloadPreparationReader.MaximumLuaCharacters) throw new InvalidDataException("Lua metadata is too large.");
                Lua(Path.GetFileName(path), await File.ReadAllBytesAsync(path, token), appId);
            }
            else if (extension == ".manifest") Depot(Path.GetFileName(path), await File.ReadAllBytesAsync(path, token));
            else if (extension == ".zip")
            {
                using var zip = ZipFile.OpenRead(path);
                ValidateArchive(zip);
                var metadata = zip.Entries.Where(entry => entry.Name.Equals("steamy.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (metadata.Length > 0)
                {
                    var available = new HashSet<int>();
                    foreach (var entry in metadata)
                    {
                        using var document = JsonDocument.Parse(await ReadEntryAsync(entry, DownloadPreparationReader.MaximumLuaCharacters, token));
                        var id = document.RootElement.GetProperty("appId").GetInt32();
                        if (id <= 0 || !available.Add(id)) throw new InvalidDataException("Ambiguous Steamy game metadata.");
                    }
                    if (appId is { } selected && !available.Contains(selected)) throw new InvalidDataException("This bundle does not contain the selected game.");
                    foreach (var id in available.Where(id => appId is null || id == appId))
                    {
                        var owned = Path.Combine(Path.GetTempPath(), "Steamy-bst-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            var import = await LocalManifestPackage.ImportAsync(path, id, owned, token);
                            Lua(id + ".lua", Utf8.GetBytes(import.Lua), id);
                            foreach (var manifest in Directory.EnumerateFiles(owned, "*.manifest")) Depot(Path.GetFileName(manifest), await File.ReadAllBytesAsync(manifest, token));
                        }
                        finally { if (Directory.Exists(owned)) Directory.Delete(owned, true); }
                    }
                }
                else
                {
                    var entries = zip.Entries.Where(entry => entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var chosen = new List<string>();
                    foreach (var entry in entries)
                    {
                        var data = await ReadEntryAsync(entry, DownloadPreparationReader.MaximumLuaCharacters, token);
                        (int AppId, string Lua) parsed;
                        try { parsed = ReadLua(Utf8.GetString(data), entry.Name); }
                        catch (InvalidDataException) when (appId is not null) { parsed = ReadLua(Utf8.GetString(data), entry.Name, appId); }
                        if (appId is { } selected && parsed.AppId != selected) continue;
                        Lua(entry.Name, data, appId);
                        chosen.Add(parsed.Lua);
                    }
                    if (entries.Length > 0 && chosen.Count == 0) throw new InvalidDataException("This ZIP does not contain the selected game.");
                    var selectedDepots = chosen.SelectMany(lua => DownloadPreparationReader.Read(lua).Depots.Select(depot => depot.DepotId)).ToHashSet();
                    foreach (var entry in zip.Entries.Where(entry => entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (appId is not null && entries.Length > 0 && (!int.TryParse(entry.Name.Split('_')[0], out var depot) || !selectedDepots.Contains(depot))) continue;
                        Depot(entry.Name, await ReadEntryAsync(entry, LocalManifestPackage.MaximumBytes, token));
                    }
                }
            }
            else throw new InvalidDataException("Select ZIP, Lua or manifest files.");
        }
        if (files.Count == 0) throw new InvalidDataException("No usable Lua or manifest files were found.");
        return new(files, ids.Order().ToArray(), files.Keys.Count(name => name.StartsWith("depotcache/", StringComparison.Ordinal)));
    }

    public static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("The archive has too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Length > 1024 || name.Contains('\\') || !names.Add(name) || name.TrimEnd('/').Split('/').Any(part => !LocalManifestPackage.SafeSegment(part)) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The archive contains an unsafe, duplicate or linked path.");
            total += entry.Length;
            if (entry.Length < 0 || total > LocalManifestPackage.MaximumBytes) throw new InvalidDataException("The expanded archive is too large.");
        }
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, long limit, CancellationToken token)
    {
        if (entry.Length > limit) throw new InvalidDataException("Archive entry exceeds the metadata size limit.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Expanded metadata exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) throw new InvalidDataException("The archive is truncated.");
        return output.ToArray();
    }
}
