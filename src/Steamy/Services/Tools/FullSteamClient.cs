using System.IO;

namespace Steamy.Services;

public static class FullSteamClient
{
    // Tiny loader/shim DLLs share the name. Require the complete x64 PE implementation.
    public static string? Find(string directory) => !Directory.Exists(directory) ? null
        : Directory.EnumerateFiles(directory, "steamclient64.dll", SearchOption.AllDirectories)
            .Where(IsFullClient).OrderByDescending(file => new FileInfo(file).Length).FirstOrDefault();
    public static bool IsFullClient(string file)
    {
        try
        {
            if (new FileInfo(file).Length < 2 * 1024 * 1024) return false;
            using var stream = File.OpenRead(file); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3c; var offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 24) return false;
            stream.Position = offset;
            return reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
