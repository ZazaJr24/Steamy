using System.Text.RegularExpressions;

namespace Steamy.Services;

public static partial class ChatMediaPolicy
{
    [GeneratedRegex("https://[^\\s<>\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex Links();
    public static bool IsImageUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && IsMediaHost(uri.Host)
        && System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
    private static bool IsMediaHost(string host) => host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
        || host is "cdn.discordapp.com" or "media.discordapp.net"
        or "static.klipy.com" or "static1.klipy.com" or "static2.klipy.com" or "media.giphy.com" or "i.giphy.com"
        || host.Length == "media0.giphy.com".Length && host.StartsWith("media", StringComparison.Ordinal) && char.IsAsciiDigit(host[5]) && host.EndsWith(".giphy.com", StringComparison.Ordinal);
    public static string? ImageUrl(string content)
    {
        foreach (Match match in Links().Matches(content))
            if (Uri.TryCreate(match.Value.TrimEnd(')', ']', '.', ',', '!'), UriKind.Absolute, out var uri) && IsImageUri(uri)) return uri.AbsoluteUri;
        return null;
    }
}
