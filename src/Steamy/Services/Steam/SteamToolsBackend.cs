using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Steamy.Services;

public static class SteamToolsBackend
{
    public const string ReceiptPath = "config/steamy-bettersteamtools.json";
    public static readonly IReadOnlyDictionary<string, string> Official104 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dwmapi.dll"] = "e1d026e903f38fed22c82be137a92cf30b1848621f6f7977c1a3e4d389687aab",
        ["xinput1_4.dll"] = "4e635c3e02d0687898342299fcb9ed698bfd2ae8ae64b3dada47ebc5dce359f7",
        ["OpenSteamTool.dll"] = "fe849c7e2b532e4e54f2d0bdce94e03f0aeffe69a64e9601a775f16fab6cdef7"
    };

    public static byte[] CreateReceipt(string version, IReadOnlyDictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version,
        files = Official104.Keys.ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(files[name])).ToLowerInvariant())
    });

    public static bool IsInstalled(string root)
    {
        try
        {
            if (Matches(root, Official104)) return true;
            var receipt = Path.Combine(root, ReceiptPath);
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 8192) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
            var hashes = document.RootElement.GetProperty("files").Deserialize<Dictionary<string, string>>();
            return hashes is not null && Official104.Keys.All(hashes.ContainsKey) && Matches(root, hashes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private static bool Matches(string root, IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var name in Official104.Keys)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) || new FileInfo(path).Length is < 2 or > 64 * 1024 * 1024) return false;
            using var stream = File.OpenRead(path);
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
            stream.Position = 0;
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hashes[name], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static Dictionary<string, byte[]> ReadArchive(byte[] archive, string expectedDigest)
    {
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BetterSteamTools SHA-256 verification failed.");
        using var memory = new MemoryStream(archive);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        SteamToolsMetadata.ValidateArchive(zip);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" })
        {
            var found = zip.Entries.Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1 || found[0].Length > 64 * 1024 * 1024) throw new InvalidDataException("Missing or ambiguous backend file: " + name);
            using var stream = found[0].Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            var bytes = output.ToArray();
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Invalid Windows backend file: " + name);
            files.Add(name, bytes);
        }
        return files;
    }

}
