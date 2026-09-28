using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>
/// Packs share candidates into ZIP archives. Every archive carries a <c>steamy.json</c> that
/// describes what is inside (app, depots, manifest ids, file hashes), so a pack can be checked
/// and indexed without opening the individual files.
/// <para>
/// Privacy: appmanifest files are cleaned before packing — the Steam account that last owned the
/// app (<c>LastOwner</c>) and the local Steam path (<c>LauncherPath</c>) never leave the machine,
/// and no local folder name is written into the metadata.
/// </para>
/// </summary>
public static class ShareArchiveBuilder
{
    private static readonly Regex PrivateAcfLine = new(
        @"^[ \t]*""(LastOwner|LauncherPath)""[^\r\n]*(\r?\n)?",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>ZIP of one app, as it is sent to the repository.</summary>
    public static byte[] BuildAppArchive(ShareCandidate candidate, string appVersion, DateTime createdUtc)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = WriteAppEntries(archive, candidate, prefix: string.Empty);
            WriteText(archive, "steamy.json", BuildMetadataJson(candidate, entries, appVersion, createdUtc));
            WriteText(archive, "README.txt", BuildReadme(new[] { candidate }, appVersion, createdUtc));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// One ZIP that holds every given app in its own folder plus an <c>index.json</c> — the
    /// "share everything at once" file for sending anywhere, no token needed.
    /// </summary>
    public static void WriteBundle(Stream output, IReadOnlyList<ShareCandidate> candidates, string appVersion, DateTime createdUtc)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var index = new List<object>();
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var folder = UniqueFolder(usedFolders, $"{candidate.AppId} - {SafeName(candidate.Name)}");
            var entries = WriteAppEntries(archive, candidate, folder + "/");
            WriteText(archive, folder + "/steamy.json", BuildMetadataJson(candidate, entries, appVersion, createdUtc));
            index.Add(new
            {
                appId = candidate.AppId,
                name = candidate.Name,
                source = SourceLabel(candidate.Source),
                folder,
                files = entries.Count,
                bytes = entries.Sum(entry => entry.Length)
            });
        }

        WriteText(archive, "index.json", JsonSerializer.Serialize(new
        {
            format = "steamy-share/1",
            createdUtc = createdUtc.ToString("O"),
            steamy = appVersion,
            apps = index
        }, JsonOptions));
        WriteText(archive, "README.txt", BuildReadme(candidates, appVersion, createdUtc));
    }

    /// <summary>Bytes of a file as they go into a share (appmanifests cleaned, everything else as is).</summary>
    public static byte[] ReadForShare(ShareFile file)
    {
        var bytes = File.ReadAllBytes(file.FullPath);
        return file.EntryName.EndsWith(".acf", StringComparison.OrdinalIgnoreCase) ? SanitizeAcf(bytes) : bytes;
    }

    /// <summary>Removes the account and local-path lines from an appmanifest.</summary>
    public static byte[] SanitizeAcf(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        var cleaned = PrivateAcfLine.Replace(text, string.Empty);
        return text == cleaned ? content : Encoding.UTF8.GetBytes(cleaned);
    }

    public static string SourceLabel(ShareSourceKind source) => source == ShareSourceKind.SteamLibrary ? "steam-library" : "dump";

    /// <summary>File-system friendly version of a game name.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var cleaned = new string((name ?? string.Empty).Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? ' ' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.');
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd();
        return cleaned.Length == 0 ? "App" : cleaned;
    }

    private sealed record EntryInfo(string Name, long Length, string Sha256);

    private static List<EntryInfo> WriteAppEntries(ZipArchive archive, ShareCandidate candidate, string prefix)
    {
        var entries = new List<EntryInfo>();
        foreach (var file in candidate.Files)
        {
            byte[] bytes;
            try
            {
                bytes = ReadForShare(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or is locked is left out instead of failing the whole pack.
                continue;
            }

            var entry = archive.CreateEntry(prefix + file.EntryName, CompressionLevel.Optimal);
            using (var stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
            entries.Add(new EntryInfo(file.EntryName, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        return entries;
    }

    private static string BuildMetadataJson(ShareCandidate candidate, IReadOnlyList<EntryInfo> entries, string appVersion, DateTime createdUtc) =>
        JsonSerializer.Serialize(new
        {
            format = "steamy-share/1",
            appId = candidate.AppId,
            name = candidate.Name,
            source = SourceLabel(candidate.Source),
            createdUtc = createdUtc.ToString("O"),
            steamy = appVersion,
            depots = candidate.Depots.Select(depot => new { depot = depot.DepotId, manifest = depot.ManifestId, size = depot.Size }),
            files = entries.Select(entry => new { name = entry.Name, bytes = entry.Length, sha256 = entry.Sha256 })
        }, JsonOptions);

    private static string BuildReadme(IReadOnlyList<ShareCandidate> candidates, string appVersion, DateTime createdUtc)
    {
        var builder = new StringBuilder()
            .AppendLine("Shared with Steamy " + appVersion)
            .AppendLine("Created: " + createdUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC")
            .AppendLine();
        foreach (var candidate in candidates)
            builder.AppendLine($"- {candidate.Name} ({candidate.AppId}) · {candidate.Files.Count} files · {SourceLabel(candidate.Source)}");
        builder.AppendLine()
            .AppendLine("steamy.json lists every depot, manifest id and file hash of the pack.")
            .AppendLine("Account data and local paths are removed before packing.");
        return builder.ToString();
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string UniqueFolder(HashSet<string> used, string folder)
    {
        var candidate = folder;
        for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{folder} ({suffix})";
        return candidate;
    }
}
