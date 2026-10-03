using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Steamy.Services;

/// <summary>Custom or replaced tools never receive Steamy-only command-line flags.</summary>
public static class BundledModCapabilities
{
    public const string MarkerFileName = "Steamy-depotdownloader-mod.json";
    private const string ForkName = "Steamy DepotDownloaderMod";

    public static bool SupportsRateLimit(string executablePath) => Supports(executablePath, "maxDownloadSpeed");

    public static bool SupportsProgress(string executablePath) => Supports(executablePath, "progressTelemetry");

    public static bool SupportsOwnFork(string executablePath)
    {
        try
        {
            using var json = ReadMarker(executablePath);
            var root = json.RootElement;
            return root.TryGetProperty("schemaVersion", out var schema) && schema.GetInt32() == 1
                && root.TryGetProperty("forkName", out var name) && name.GetString() == ForkName
                && root.TryGetProperty("forkVersion", out var version) && Version.TryParse(version.GetString(), out _)
                && root.TryGetProperty("sourceSha256", out var source) && IsSha256(source.GetString())
                && MatchesExecutable(executablePath, root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return false; }
    }

    public static bool SupportsGracefulStop(string executablePath) =>
        SupportsOwnFork(executablePath) && Supports(executablePath, "gracefulStop");

    public static string SelectExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;
        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "DepotDownloaderMod",
            "Release", "net9.0", "DepotDownloaderMod.exe");
        return File.Exists(bundled) && SupportsOwnFork(bundled) ? bundled : string.Empty;
    }

    private static bool Supports(string executablePath, string capability)
    {
        try
        {
            using var json = ReadMarker(executablePath);
            var root = json.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1
                || !root.TryGetProperty(capability, out var speed) || speed.ValueKind != JsonValueKind.True)
                return false;
            // The marker is valid only for the binary shipped with it. Selecting or replacing
            // an upstream tool in the same folder cannot silently enable an unsupported flag.
            return MatchesExecutable(executablePath, root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static JsonDocument ReadMarker(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory)) throw new FileNotFoundException();
        var marker = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 8192) throw new FileNotFoundException();
        return JsonDocument.Parse(File.ReadAllText(marker));
    }

    private static bool MatchesExecutable(string executablePath, JsonElement root)
    {
        var directory = Path.GetDirectoryName(executablePath);
        return MatchesHash(executablePath, root, "exeSha256")
            && (root.TryGetProperty("singleFile", out var singleFile) && singleFile.ValueKind == JsonValueKind.True
                || MatchesHash(Path.Combine(directory!, "DepotDownloaderMod.dll"), root, "dllSha256"));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool AddRateLimit(ICollection<string> arguments, string executablePath, int limitMiB)
    {
        if (limitMiB <= 0 || !SupportsRateLimit(executablePath)) return false;
        arguments.Add("-max-download-speed");
        arguments.Add(((long)Math.Clamp(limitMiB, 1, 1024) * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool MatchesHash(string path, JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var expected) || expected.ValueKind != JsonValueKind.String
            || expected.GetString() is not { Length: 64 } checksum || !File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(checksum, StringComparison.OrdinalIgnoreCase);
    }
}
