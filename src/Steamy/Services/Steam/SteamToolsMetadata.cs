using SharpCompress.Archives;
using SharpCompress.Common;
using System.Globalization;
using System.Buffers.Binary;
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
    private static readonly Regex Statements = new(
        @"\G\s*(?:(?<call>(?<name>addappid|setManifestid)\s*\((?<args>[^)]*)\))|if\s+(?<guard>addappid|setManifestid)\s+then\b|(?<end>end\b)|(?<separator>;))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Add = new("^\\s*(\\d+)\\s*(?:,\\s*(\\d+)\\s*(?:,\\s*(?<quote>[\"'])([a-fA-F0-9]{8,128})\\k<quote>\\s*)?)?$", RegexOptions.None, Timeout);
    private static readonly Regex Manifest = new(
        "^\\s*(\\d+)\\s*,\\s*(?:(?<quote>[\"'])(?<manifest>\\d+)\\k<quote>|(?<manifest>\\d+))\\s*(?:,\\s*(?<size>\\d+)\\s*)?$",
        RegexOptions.None, Timeout);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static (int AppId, string Lua) ReadLua(string content, string fileName, int? requestedAppId = null,
        bool allowRequestedAppWithoutRootEntry = false)
    {
        if (content.Length > DownloadPreparationReader.MaximumLuaCharacters) throw new InvalidDataException("Lua metadata is too large.");
        var active = Comments.Replace(content.TrimStart('\uFEFF'), "");
        var calls = ReadMetadataStatements(active);
        var ids = new HashSet<int>();
        var referencedIds = new HashSet<int>();
        var output = new StringBuilder();
        foreach (Match call in calls)
        {
            var isAdd = call.Groups["name"].Value.Equals("addappid", StringComparison.OrdinalIgnoreCase);
            var args = (isAdd ? Add : Manifest).Match(call.Groups["args"].Value);
            if (!args.Success || !int.TryParse(args.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidDataException("The Lua metadata has an invalid app or depot ID.");
            if (isAdd)
            {
                // A three-argument addappid is an encrypted app/depot entry. It still names a
                // valid app ID, even though it is not an unencrypted root game entry. Keep it
                // available for an explicitly requested App ID (for example DepotBox Lua).
                referencedIds.Add(id);
                if (!args.Groups[3].Success) ids.Add(id);
                var flag = args.Groups[2].Success ? args.Groups[2].Value : null;
                if (flag is not null && !uint.TryParse(flag, out _)) throw new InvalidDataException("Invalid addappid flag.");
                output.AppendLine(flag is null ? $"addappid({id})" : args.Groups[3].Success
                    ? $"addappid({id},{flag},\"{args.Groups[3].Value}\")" : $"addappid({id},{flag})");
            }
            else
            {
                if (!DownloadPreparationReader.IsManifestId(args.Groups["manifest"].Value)) throw new InvalidDataException("Invalid manifest ID.");
                if (args.Groups["size"].Success && !ulong.TryParse(args.Groups["size"].Value, out _)) throw new InvalidDataException("Invalid manifest size.");
                output.AppendLine($"setManifestid({id},\"{args.Groups["manifest"].Value}\"{(args.Groups["size"].Success ? "," + args.Groups["size"].Value : "")})");
            }
        }
        var named = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"^(\d+)(?:\D|$)", RegexOptions.None, Timeout);
        int? fromName = named.Success && int.TryParse(named.Groups[1].Value, out var parsed) && referencedIds.Contains(parsed) ? parsed : null;
        int? appId = requestedAppId is { } requested
            ? referencedIds.Contains(requested) || allowRequestedAppWithoutRootEntry && ids.Count == 0 ? requested : null
            : fromName ?? (ids.Count == 1 ? ids.Single() : ids.Count == 0 && referencedIds.Count == 1 ? referencedIds.Single() : (int?)null);
        var appIdIsExplicitProviderFallback = requestedAppId == appId && allowRequestedAppWithoutRootEntry && ids.Count == 0;
        if (appId is null || !referencedIds.Contains(appId.Value) && !appIdIsExplicitProviderFallback)
            throw new InvalidDataException("The game ID is missing or ambiguous. Enter its Steam App ID.");
        if (requestedAppId is { } apiAppId && !ids.Contains(apiAppId) && allowRequestedAppWithoutRootEntry)
            output.Insert(0, $"addappid({apiAppId}){Environment.NewLine}");
        return (appId.Value, output.ToString());
    }

    private static IReadOnlyList<Match> ReadMetadataStatements(string active)
    {
        var calls = new List<Match>();
        int position = 0, depth = 0, statements = 0;
        while (position < active.Length)
        {
            var statement = Statements.Match(active, position);
            if (!statement.Success && active.AsSpan(position).Trim().IsEmpty) break;
            if (!statement.Success || statement.Index != position || ++statements > DownloadPreparationReader.MaximumEntries * 4)
                throw new InvalidDataException("The file contains unsupported Lua code. Select addappid/setManifestid metadata.");
            position += statement.Length;
            if (statement.Groups["guard"].Success)
            {
                // Only positive function-existence guards are accepted. No Lua is evaluated,
                // and only validated calls are written to the installed backend script.
                if (++depth > 32) throw new InvalidDataException("Lua metadata guards are nested too deeply.");
            }
            else if (statement.Groups["end"].Success)
            {
                if (--depth < 0) throw new InvalidDataException("Lua metadata contains an unmatched end.");
            }
            else if (statement.Groups["call"].Success)
            {
                if (calls.Count >= DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("Lua metadata has too many entries.");
                calls.Add(statement);
            }
        }
        if (depth != 0) throw new InvalidDataException("Lua metadata has an unclosed compatibility guard.");
        if (calls.Count == 0) throw new InvalidDataException("The file contains no addappid/setManifestid metadata.");
        return calls;
    }

    public static async Task<SteamToolsMetadataPlan> ReadAsync(IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default)
    {
        if (paths.Count is 0 or > DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("Select ZIP, 7z, RAR, Lua or manifest files.");
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
            name = CanonicalManifestName(name, data);
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
            else if (extension is ".7z" or ".rar")
            {
                // Decode in one pass, including solid archives. Reuse the ZIP metadata reader so
                // selection, validation and the final atomic Steam write behave identically.
                var temporary = Path.Combine(Path.GetTempPath(), "Steamy-metadata-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    await Task.Run(() => NormalizeArchive(path, temporary, token), token);
                    var imported = await ReadAsync([temporary], appId, token);
                    foreach (var file in imported.Files) Put(file.Key, file.Value);
                    ids.UnionWith(imported.AppIds);
                }
                catch (SharpCompress.Common.CryptographicException exception)
                { throw new InvalidDataException("Encrypted metadata archives are not supported. Extract them first.", exception); }
                catch (SharpCompressException exception)
                { throw new InvalidDataException("The metadata archive is unsupported or damaged.", exception); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
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
                        var data = await ReadEntryAsync(entry, LocalManifestPackage.MaximumBytes, token);
                        var name = CanonicalManifestName(entry.Name, data);
                        if (appId is not null && entries.Length > 0 && (!int.TryParse(name.Split('_')[0], out var depot) || !selectedDepots.Contains(depot))) continue;
                        Depot(name, data);
                    }
                }
            }
            else throw new InvalidDataException("Select ZIP, 7z, RAR, Lua or manifest files.");
        }
        if (files.Count == 0) throw new InvalidDataException("No usable Lua or manifest files were found.");
        return new(files, ids.Order().ToArray(), files.Keys.Count(name => name.StartsWith("depotcache/", StringComparison.Ordinal)));
    }

    // Some providers name a real Steam depot manifest after the game. Its binary metadata,
    // rather than an invented ID or the archive name, supplies the canonical cache filename.
    private static string CanonicalManifestName(string name, byte[] data)
    {
        if (!Regex.IsMatch(name, @"^\d+\.manifest$", RegexOptions.IgnoreCase, Timeout)) return name;
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x71F617D0)
            throw new InvalidDataException("The manifest has no valid Steam depot identity.");
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (payloadLength > data.Length - 16) throw new InvalidDataException("The manifest is truncated.");
        var offset = checked(8 + (int)payloadLength);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != 0x1F4812BE)
            throw new InvalidDataException("The manifest has no Steam metadata section.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        if (length > 65536 || length > data.Length - offset - 8)
            throw new InvalidDataException("The manifest metadata is invalid or truncated.");
        var metadata = data.AsSpan(offset + 8, (int)length);
        ulong depot = 0, gid = 0;
        var position = 0;
        while (position < metadata.Length)
        {
            var tag = ReadManifestVarint(metadata, ref position);
            var field = tag >> 3;
            if (field == 0) throw new InvalidDataException("Invalid manifest metadata field.");
            if ((tag & 7) == 0)
            {
                var value = ReadManifestVarint(metadata, ref position);
                if (field == 1) { if (depot != 0 && depot != value) throw new InvalidDataException("Ambiguous depot ID."); depot = value; }
                if (field == 2) { if (gid != 0 && gid != value) throw new InvalidDataException("Ambiguous manifest ID."); gid = value; }
            }
            else
            {
                if (field is 1 or 2) throw new InvalidDataException("Invalid manifest identity field.");
                var skip = (tag & 7) switch
                {
                    1 => 8UL,
                    2 => ReadManifestVarint(metadata, ref position),
                    5 => 4UL,
                    _ => throw new InvalidDataException("Unsupported manifest metadata field.")
                };
                if (skip > (ulong)(metadata.Length - position)) throw new InvalidDataException("Truncated manifest metadata field.");
                position += (int)skip;
            }
        }
        if (depot is 0 or > int.MaxValue || gid == 0) throw new InvalidDataException("Missing manifest depot or version ID.");
        return $"{depot}_{gid}.manifest";
    }

    private static ulong ReadManifestVarint(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (position >= data.Length) throw new InvalidDataException("Truncated manifest metadata.");
            var next = data[position++];
            if (shift == 63 && next > 1) throw new InvalidDataException("Manifest metadata number overflows.");
            value |= (ulong)(next & 127) << shift;
            if (next < 128) return value;
        }
        throw new InvalidDataException("Invalid manifest metadata number.");
    }

    private static void NormalizeArchive(string path, string target, CancellationToken token)
    {
        using var archive = ArchiveFactory.OpenArchive(path);
        var entries = archive.Entries.ToArray();
        if (entries.Length > DownloadPreparationReader.MaximumEntries)
            throw new InvalidDataException("The archive has too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var name = (entry.Key ?? "").Replace('\\', '/');
            if (name.Length > 1024 || !names.Add(name)
                || name.TrimEnd('/').Split('/').Any(part => !LocalManifestPackage.SafeSegment(part))
                || !string.IsNullOrEmpty(entry.LinkTarget))
                throw new InvalidDataException("The archive contains an unsafe, duplicate or linked path.");
            if (entry.IsEncrypted) throw new InvalidDataException("Encrypted metadata archives are not supported. Extract them first.");
            if (entry.Size < 0 || entry.Size > LocalManifestPackage.MaximumBytes - total)
                throw new InvalidDataException("The expanded archive is too large.");
            total += entry.Size;
        }
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        long expanded = 0;
        void CopyEntry(IEntry entry, Func<Stream> open)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) return;
            var key = (entry.Key ?? throw new InvalidDataException("Missing archive path.")).Replace('\\', '/');
            var extension = Path.GetExtension(key).ToLowerInvariant();
            var metadata = extension is ".lua" or ".manifest" || Path.GetFileName(key).Equals("steamy.json", StringComparison.OrdinalIgnoreCase);
            var limit = extension is ".lua" or ".json" ? DownloadPreparationReader.MaximumLuaCharacters : LocalManifestPackage.MaximumBytes;
            if (entry.Size > limit) throw new InvalidDataException("Archive entry exceeds the metadata size limit.");
            using var input = open();
            using var destination = metadata ? output.CreateEntry(key, CompressionLevel.NoCompression).Open() : Stream.Null;
            var buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                copied += count;
                expanded += count;
                if (copied > limit || expanded > LocalManifestPackage.MaximumBytes)
                    throw new InvalidDataException("Expanded metadata exceeds its size limit.");
                destination.Write(buffer, 0, count);
            }
            if (copied != entry.Size) throw new InvalidDataException("The archive is truncated.");
        }
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry()) CopyEntry(reader.Entry, () => reader.OpenEntryStream());
        }
        else
            foreach (var entry in entries) CopyEntry(entry, () => entry.OpenEntryStream());
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
