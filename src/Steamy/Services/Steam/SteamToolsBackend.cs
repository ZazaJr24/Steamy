using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Steamy.Services;

public static class SteamToolsBackend
{
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
