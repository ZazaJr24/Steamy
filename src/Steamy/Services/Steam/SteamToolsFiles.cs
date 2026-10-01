using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>Stages all files before writes, retains originals, and rolls back an interrupted install.</summary>
public static class SteamToolsFiles
{
    public static string RegisterLuaPath(string existing)
    {
        const string path = "config/stplug-in";
        var section = Regex.Match(existing, @"(?ms)^\s*\[lua\][^\r\n]*(?:\r?\n|\z)(?<body>.*?)(?=^\s*\[|\z)", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        if (!section.Success) return existing.TrimEnd() + (existing.Length > 0 ? "\n\n" : "") + "[lua]\npaths = [\"" + path + "\"]\n";
        var body = section.Groups["body"];
        var key = Regex.Match(body.Value, @"(?m)^\s*paths\s*=\s*\[", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        if (!key.Success)
        {
            if (Regex.IsMatch(body.Value, @"(?m)^[ \t]*paths[ \t]*=")) throw new InvalidDataException("The existing Lua paths setting must be an array.");
            return existing.Insert(body.Index, (body.Index > 0 && existing[body.Index - 1] != '\n' ? "\n" : "") + "paths = [\"" + path + "\"]\n");
        }
        var start = body.Index + key.Index + key.Length;
        var quote = '\0';
        var escaped = false;
        var comment = false;
        var close = -1;
        var quoteStart = -1;
        var tokens = new List<(string Value, int End)>();
        for (var i = start; i < body.Index + body.Length; i++)
        {
            var ch = existing[i];
            if (comment) { if (ch == '\n') comment = false; continue; }
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\' && quote == '"') { escaped = true; continue; }
                if (ch == quote) { tokens.Add((existing[(quoteStart + 1)..i], i + 1)); quote = '\0'; }
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; quoteStart = i; }
            else if (ch == '#') comment = true;
            else if (ch == ']') { close = i; break; }
        }
        if (close < 0) throw new InvalidDataException("The existing opensteamtool.toml paths array is incomplete. Repair it before installing.");
        if (tokens.Any(item => string.Join('/', item.Value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)).Equals(path, StringComparison.OrdinalIgnoreCase))) return existing;
        if (tokens.Count > 0)
        {
            var last = tokens[^1].End;
            var trailing = Regex.Replace(existing[last..close], @"(?m)#[^\r\n]*", "", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
            if (trailing.Length > 0 && trailing != ",") throw new InvalidDataException("Unsupported paths array in opensteamtool.toml.");
            if (trailing.Length == 0) { existing = existing.Insert(last, ","); close++; }
        }
        else if (Regex.Replace(existing[start..close], @"(?m)#[^\r\n]*", "", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim().Length > 0)
            throw new InvalidDataException("Unsupported paths array in opensteamtool.toml.");
        return existing.Insert(close, "\n  \"" + path + "\"\n");
    }

    public static string Apply(string root, IReadOnlyDictionary<string, byte[]> files, Action<string, byte[]>? writer = null, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "steam.exe"))) throw new InvalidDataException("Choose the Steam installation folder containing steam.exe.");
        if (files.Count == 0) throw new InvalidDataException("There are no files to install.");
        writer ??= AtomicWrite;
        var targets = new List<(string Relative, string Path, byte[] Data, byte[]? Original)>();
        foreach (var item in files)
        {
            if (item.Key.Contains('\\') || item.Key.Split('/').Any(segment => !LocalManifestPackage.SafeSegment(segment))) throw new InvalidDataException("Invalid install target.");
            var path = Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(root, path);
            if (Directory.Exists(path)) throw new IOException("An install target is a directory: " + item.Key);
            if (File.Exists(path) && new FileInfo(path).Length > LocalManifestPackage.MaximumBytes) throw new IOException("An existing install target is too large to back up.");
            targets.Add((item.Key, path, item.Value, File.Exists(path) ? File.ReadAllBytes(path) : null));
        }
        token.ThrowIfCancellationRequested();
        var backup = Path.Combine(root, "config", "steamy-backups", Guid.NewGuid().ToString("N"));
        RejectLinks(root, Path.Combine(backup, "files.json"));
        Directory.CreateDirectory(backup);
        foreach (var target in targets.Where(item => item.Original is not null))
        {
            var path = Path.Combine(backup, target.Relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, target.Original!);
        }
        File.WriteAllText(Path.Combine(backup, "files.json"), JsonSerializer.Serialize(targets.Select(item => new
        { path = item.Relative, hadOriginal = item.Original is not null, installedSha256 = Convert.ToHexString(SHA256.HashData(item.Data)).ToLowerInvariant() }), new JsonSerializerOptions { WriteIndented = true }));
        var touched = new List<(string Path, byte[]? Original)>();
        try
        {
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(root, target.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                touched.Add((target.Path, target.Original));
                writer(target.Path, target.Data);
            }
            File.WriteAllText(Path.Combine(backup, "result.txt"), "Completed " + DateTimeOffset.UtcNow.ToString("O"));
            return backup;
        }
        catch (Exception cause)
        {
            var errors = new List<Exception>();
            foreach (var original in touched.AsEnumerable().Reverse())
            {
                try
                {
                    RejectLinks(root, original.Path);
                    if (original.Original is null) { if (File.Exists(original.Path)) File.Delete(original.Path); }
                    else AtomicWrite(original.Path, original.Original);
                }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0) throw new IOException("Installation failed and some originals could not be restored. Backups: " + backup, new AggregateException(errors.Prepend(cause)));
            throw;
        }
    }

    public static void RejectLinks(string root, string target)
    {
        var current = new DirectoryInfo(root);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Steam folders are not supported for installation.");
            current = current.Parent;
        }
        var relative = Path.GetRelativePath(root, target);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) throw new IOException("Install target escapes the Steam folder.");
        var path = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            path = Path.Combine(path, segment);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked install targets are not supported.");
        }
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        var staging = path + ".steamy-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, data);
            File.Move(staging, path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
