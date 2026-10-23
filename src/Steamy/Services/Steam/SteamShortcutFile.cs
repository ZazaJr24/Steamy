using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace Steamy.Services;

/// <summary>Append-only binary KeyValues editing; all existing entries remain byte-for-byte intact.</summary>
internal static class SteamShortcutFile
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal sealed record Result(bool Added, string? BackupPath);

    internal static Result Add(string path, string name, string executable, string arguments)
    {
        if (new[] { name, executable, arguments }.Any(value => value.Contains('\0'))) throw new ArgumentException("A shortcut cannot contain a null character.");
        var exists = File.Exists(path);
        if (exists && new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The Steam shortcuts file is too large.");
        var original = exists ? File.ReadAllBytes(path) : [0, .. Utf8.GetBytes("shortcuts"), 0, 8, 8];
        var reader = new Reader(original);
        if (reader.Byte() != 0 || reader.String() != "shortcuts") throw new InvalidDataException("Unrecognized Steam shortcuts file.");
        var lastIndex = -1; var ids = new HashSet<uint>(); var duplicate = false;
        while (reader.Peek() != 8)
        {
            if (reader.Byte() != 0 || !int.TryParse(reader.String(), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index > 100000)
                throw new InvalidDataException("Unrecognized Steam shortcut entry.");
            lastIndex = Math.Max(lastIndex, index);
            string? target = null;
            reader.Object(0, (key, type, value) =>
            {
                if (key.Equals("exe", StringComparison.OrdinalIgnoreCase) && type == 1) target = Utf8.GetString(value);
                if (key.Equals("appid", StringComparison.OrdinalIgnoreCase) && type == 2) ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(value));
            });
            duplicate |= target is not null && target.Trim('"').Replace('/', '\\').Equals(executable.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }
        var insertion = reader.Position;
        reader.Byte();
        if (reader.Position < original.Length && reader.Byte() != 8 || reader.Position != original.Length)
            throw new InvalidDataException("Unexpected data after the Steam shortcuts object.");
        if (duplicate) return new(false, null);
        var quoted = "\"" + executable + "\"";
        var id = ShortcutId(quoted + name);
        if (ids.Contains(id)) throw new InvalidDataException("A different Steam shortcut already uses this identifier.");
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, insertion));
        using (var writer = new BinaryWriter(output, Utf8, leaveOpen: true))
        {
            void Text(string value) { writer.Write(Utf8.GetBytes(value)); writer.Write((byte)0); }
            void String(string key, string value) { writer.Write((byte)1); Text(key); Text(value); }
            void Number(string key, uint value) { writer.Write((byte)2); Text(key); writer.Write(value); }
            writer.Write((byte)0); Text((lastIndex + 1).ToString(CultureInfo.InvariantCulture));
            Number("appid", id); String("AppName", name); String("Exe", quoted);
            String("StartDir", "\"" + Path.GetDirectoryName(executable) + "\"");
            String("icon", executable); String("ShortcutPath", ""); String("LaunchOptions", arguments);
            Number("IsHidden", 0); Number("AllowDesktopConfig", 1); Number("AllowOverlay", 1);
            Number("OpenVR", 0); Number("Devkit", 0); String("DevkitGameID", "");
            Number("DevkitOverrideAppID", 0); Number("LastPlayTime", 0); String("FlatpakAppID", "");
            writer.Write((byte)0); Text("tags"); writer.Write((byte)8); writer.Write((byte)8);
        }
        output.Write(original.AsSpan(insertion));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".steamy-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = exists ? path + ".steamy-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak" : null;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Position = 0; output.CopyTo(file); file.Flush(flushToDisk: true); }
            if (exists)
            {
                if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(path))) throw new IOException("Steam shortcuts changed while adding the game. Try again.");
                File.Replace(temporary, path, backup);
            }
            else File.Move(temporary, path);
            return new(true, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static uint ShortcutId(string value)
    {
        var crc = uint.MaxValue;
        foreach (var item in Utf8.GetBytes(value))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc | 0x80000000;
    }
    private sealed class Reader(byte[] data)
    {
        internal int Position { get; private set; }
        internal byte Peek() => Position < data.Length ? data[Position] : throw new InvalidDataException("Truncated Steam shortcuts file.");
        internal byte Byte() { var value = Peek(); Position++; return value; }
        internal string String()
        {
            var start = Position;
            while (Byte() != 0) { }
            return Utf8.GetString(data, start, Position - start - 1);
        }
        internal void Object(int depth, Action<string, byte, byte[]>? field = null)
        {
            if (depth > 32) throw new InvalidDataException("Steam shortcuts nesting is too deep.");
            while (Peek() != 8)
            {
                var type = Byte(); var key = String(); var start = Position;
                if (type == 0) { Object(depth + 1); continue; }
                if (type == 1) String();
                else if (type == 5) { while (Byte() != 0 | Byte() != 0) { } }
                else
                {
                    var size = type switch { 2 or 3 or 4 or 6 => 4, 7 => 8, _ => throw new InvalidDataException("Unsupported Steam shortcuts value type.") };
                    for (var i = 0; i < size; i++) Byte();
                }
                if (field is not null) field(key, type, data.AsSpan(start, Position - start - (type == 1 ? 1 : 0)).ToArray());
            }
            Byte();
        }
    }
}
