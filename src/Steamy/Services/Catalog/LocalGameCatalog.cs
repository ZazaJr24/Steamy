using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using Steamy.Models;

namespace Steamy.Services;

public sealed record LocalGamePackage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "Fix";
    public string Name { get; init; } = "";
    public string AppId { get; init; } = "";
    public string ArchivePath { get; init; } = "";
    public string Version { get; init; } = "";
    public string Notes { get; init; } = "";
    public string[] Steps { get; init; } = [];
    public string Label => $"{Kind} · {Name}" + (Version.Length == 0 ? "" : $" · {Version}");
    public string VersionLabel => Version.Length > 0 ? Version : "Default package";
    public bool Available => File.Exists(ArchivePath);
}

/// <summary>Local metadata only. Archives stay in their original folder; no scripts are executed.</summary>
public sealed class LocalGameCatalog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string DefaultPath => Environment.GetEnvironmentVariable("STEAMY_LOCAL_CATALOG") is { Length: > 0 } path
        ? Path.GetFullPath(path) : Path.Combine(Steamy.Storage.AppDataPaths.Root, "local-catalog", "catalog.json");
    public string CatalogPath { get; }
    public LocalGameCatalog(string? path = null) => CatalogPath = Path.GetFullPath(path ?? DefaultPath);

    public async Task<List<LocalGamePackage>> ReadAsync(CancellationToken token = default)
    {
        if (!File.Exists(CatalogPath)) return [];
        await using var stream = new FileStream(CatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("The local catalog is too large.");
        var items = await JsonSerializer.DeserializeAsync<List<LocalGamePackage>>(stream, Json, token)
            ?? throw new InvalidDataException("The local catalog is empty or invalid.");
        items = items.Where(item => item.Kind != "Hypervisor").ToList();
        foreach (var item in items) Validate(item);
        if (items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidDataException("The local catalog contains duplicate IDs.");
        return items;
    }

    public async Task UpsertAsync(LocalGamePackage package, CancellationToken token = default)
    {
        Validate(package);
        await MutateAsync(items => { items.RemoveAll(item => item.Id == package.Id); items.Add(package); }, token);
    }

    public Task RemoveAsync(string id, CancellationToken token = default) => MutateAsync(items => items.RemoveAll(item => item.Id == id), token);

    public async Task RetireLegacyEntriesAsync(CancellationToken token = default)
    {
        if (!File.Exists(CatalogPath)) return;
        await MutateAsync(_ => { }, token, retireLegacy: true);
    }
    private async Task MutateAsync(Action<List<LocalGamePackage>> change, CancellationToken token, bool retireLegacy = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        // The editor can run separately from the app. Serialize read/modify/write across processes.
        FileStream? lease = null;
        for (var attempt = 0; lease is null; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { lease = new FileStream(CatalogPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 30) { await Task.Delay(50, token); }
        }
        await using (lease)
        {
            if (retireLegacy)
            {
                var original = await File.ReadAllBytesAsync(CatalogPath, token);
                var legacy = JsonSerializer.Deserialize<List<LocalGamePackage>>(original, Json) ?? throw new InvalidDataException("Invalid catalog.");
                if (!legacy.Any(item => item.Kind == "Hypervisor")) return;
                await File.WriteAllBytesAsync(CatalogPath + ".before-hypervisor-removal-" + Guid.NewGuid().ToString("N") + ".json", original, token);
            }
            var items = await ReadAsync(token);
            change(items);
            if (items.Count > 10000) throw new InvalidDataException("At most 10,000 local packages can be saved.");
            var temporary = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(items.OrderBy(item => item.Name).ThenBy(item => item.Version), Json), token);
                if (File.Exists(CatalogPath)) File.Replace(temporary, CatalogPath, CatalogPath + ".bak");
                else File.Move(temporary, CatalogPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static void Validate(LocalGamePackage item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 80
            || item.Kind != "Fix" || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 250
            || item.AppId is null || item.AppId.Length > 0 && (!int.TryParse(item.AppId, out var id) || id <= 0)
            || string.IsNullOrWhiteSpace(item.ArchivePath) || !Path.IsPathFullyQualified(item.ArchivePath)
            || !IsArchive(item.ArchivePath) || item.Version is null || item.Version.Length > 250
            || item.Notes is null || item.Notes.Length > 60000 || item.Steps is null || item.Steps.Length > 64
            || item.Steps.Any(step => string.IsNullOrWhiteSpace(step) || step.Length > 4000))
            throw new InvalidDataException("Use a name, category, optional numeric Steam App ID, absolute ZIP/7z/RAR path and valid instructions.");
    }

    internal static bool IsArchive(string path) => new[] { ".zip", ".7z", ".rar" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<int> ImportFolderAsync(string folder, string kind, CancellationToken token = default)
    {
        if (kind != "Fix") throw new ArgumentException("Choose a game-fix package.");
        var paths = Directory.GetFiles(folder).Where(IsArchive).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var packages = await Task.Run(() => paths.Select(path => { token.ThrowIfCancellationRequested(); return DescribeArchive(path, kind); }).ToArray(), token);
        var added = 0;
        await MutateAsync(items =>
        {
            foreach (var package in packages)
            {
                if (items.Any(item => item.Kind == kind && string.Equals(item.ArchivePath, package.ArchivePath, StringComparison.OrdinalIgnoreCase))) continue;
                items.Add(package); added++;
            }
        }, token);
        return added;
    }

    internal static LocalGamePackage DescribeArchive(string path, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var release = Regex.Replace(stem, @"-DenuvOwO.*$", "", RegexOptions.IgnoreCase);
        var version = Regex.Match(release, @"(?:[.-])((?:Build[.-]?|b)\d+|v\d[\w.-]*|Update[.-][\w.-]+)$", RegexOptions.IgnoreCase);
        var title = (version.Success ? release[..version.Index] : release).Replace('.', ' ').Replace('_', ' ').Trim();
        var notes = "";
        var appId = "";
        // Only read small text/metadata entries. Never extract or launch archive contents.
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var candidates = archive.Entries.Where(entry => !entry.IsDirectory && entry.Size is > 0 and <= 65536
                && Regex.IsMatch(entry.Key ?? "", @"(?:[^/\\]+\.nfo|readme[^/\\]*\.(?:txt|md)|steam_appid\.txt|steam_api[^/\\]*\.ini|steam_emu\.ini|configs\.app\.ini)$", RegexOptions.IgnoreCase)).Take(12);
            foreach (var entry in candidates)
            {
                using var reader = new StreamReader(entry.OpenEntryStream(), Encoding.UTF8, true);
                var buffer = new char[65536];
                var text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
                var isInstructions = (entry.Key ?? "").Contains("readme", StringComparison.OrdinalIgnoreCase) || (entry.Key ?? "").EndsWith(".nfo", StringComparison.OrdinalIgnoreCase);
                if (isInstructions)
                    notes = (notes + (notes.Length == 0 ? "" : "\n\n") + text).Trim();
                if (isInstructions) continue;
                var match = Regex.Match(text, @"(?im)^\s*(?:App_?Id\s*=\s*)?(\d{3,10})\s*$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0) appId = id.ToString();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { notes = "Archive instructions could not be read. Add the package-specific instructions in the local editor."; }
        notes = notes.Replace("\r\r\n", "\n").Replace("\r\n", "\n");
        var instructionsAt = notes.IndexOf("READ EVERYTHING BELOW", StringComparison.OrdinalIgnoreCase);
        if (instructionsAt >= 0)
        {
            notes = notes[instructionsAt..];
            var thanksAt = notes.IndexOf("SPECIAL THANKS", StringComparison.OrdinalIgnoreCase);
            if (thanksAt > 0) notes = notes[..thanksAt].Trim();
        }
        return new LocalGamePackage
        {
            Kind = kind, Name = title, Version = version.Success ? version.Groups[1].Value.Replace('.', ' ') : "",
            ArchivePath = Path.GetFullPath(path), AppId = appId, Notes = notes.Length > 60000 ? notes[..60000] : notes,
            Steps = ["Confirm the game build matches this package version.", "Close the game and back up the files you intend to replace.",
                "Read the package instructions below before making any changes.", "Open the package location and follow its instructions manually."]
        };
    }

    public async Task<IReadOnlyList<FixGame>> ReadFixesAsync(CancellationToken token = default)
    {
        var packages = await ReadAsync(token);
        return packages.Where(item => item.Kind == "Fix")
            .GroupBy(item => string.IsNullOrEmpty(item.AppId) ? "local:" + item.Name.ToUpperInvariant() : item.AppId)
            .Select(group => new FixGame { AppId = group.Key, Name = group.First().Name,
                Fixes = group.Select(item => new FixEntry { Filename = Path.GetFileName(item.ArchivePath),
                    LocalArchivePath = item.ArchivePath, Instructions = string.Join("\n", item.Steps) + "\n\n" + item.Notes,
                    Badges = new[] { "Local", item.Version }.Where(value => value.Length > 0).ToArray() }).ToArray() }).ToArray();
    }
}
