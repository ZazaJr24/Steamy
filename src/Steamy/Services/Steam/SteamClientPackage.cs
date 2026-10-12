using System.IO;
using System.Text;

namespace Steamy.Services;

/// <summary>Copies provider scripts for the Steam client without interpreting or executing Lua.</summary>
internal static class SteamClientPackage
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string ValidateScript(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || Utf8.GetByteCount(content) > DownloadPreparationReader.MaximumLuaCharacters)
            throw new InvalidDataException("The provider returned empty or oversized Lua content.");
        var beginning = content.TrimStart('\uFEFF').TrimStart();
        if (beginning.StartsWith('<') || beginning.StartsWith('{') || beginning.StartsWith('[') || content.Contains('\0'))
            throw new InvalidDataException("The provider returned an API response or binary file instead of Lua.");
        return content;
    }

    internal static async Task<string> ReadScriptAsync(string directory, int appId, CancellationToken token)
    {
        var scripts = Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase))
            .Take(DownloadPreparationReader.MaximumEntries + 1).ToArray();
        if (scripts.Length > DownloadPreparationReader.MaximumEntries) throw new InvalidDataException("The package has too many Lua files.");
        var selected = scripts.SingleOrDefault(path => Path.GetFileName(path).Equals($"{appId}.lua", StringComparison.OrdinalIgnoreCase));
        if (selected is null && scripts.Length == 1 && !int.TryParse(Path.GetFileNameWithoutExtension(scripts[0]), out _)) selected = scripts[0];
        if (selected is null) throw new InvalidDataException("The archive has no unambiguous Lua file for the requested app.");
        if (new FileInfo(selected).Length > DownloadPreparationReader.MaximumLuaCharacters) throw new InvalidDataException("The Lua file is too large.");
        return ValidateScript(Utf8.GetString(await File.ReadAllBytesAsync(selected, token)));
    }

    internal static async Task<SteamToolsMetadataPlan> ReadAsync(int appId, string script, IReadOnlyList<string> manifests, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (appId <= 0) throw new InvalidDataException("Choose a valid app ID.");
        var original = Utf8.GetBytes(ValidateScript(script));
        var plan = manifests.Count == 0 ? new SteamToolsMetadataPlan(new Dictionary<string, byte[]>(), [], 0)
            : await SteamToolsMetadata.ReadAsync(manifests, appId, token);
        var files = new Dictionary<string, byte[]>(plan.Files, StringComparer.OrdinalIgnoreCase)
        { [$"config/stplug-in/{appId}.lua"] = original };
        return new SteamToolsMetadataPlan(files, [appId], plan.ManifestCount);
    }
}
