using System.Text.RegularExpressions;

namespace Steamy.Services;

internal static class GameClassification
{
    private static readonly Regex NsfwNameRegex = new(
        @"\b(?:nsfw|hentai|ecchi|erotica|erotic|porn|nudity|futanari|lewd|uncensored|milf|waifu\s+uncensored|adult\s*(?:content|game|edition|only|patch|nsfw)|18\+)\b|🔞",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NsfwGenreRegex = new(
        @"\b(?:nsfw|hentai|erotica|erotic|nudity|sexual\s*content|mature\s*/\s*sexual|sexual)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsNsfw(string? name, string? genres = null)
    {
        if (!string.IsNullOrWhiteSpace(name) && NsfwNameRegex.IsMatch(name)) return true;
        if (!string.IsNullOrWhiteSpace(genres) && NsfwGenreRegex.IsMatch(genres)) return true;
        return false;
    }

    // Legacy Call of Duty installs expose their multiplayer/OSX companion as
    // separate Steam apps classified "game". They are not another Windows base game.
    public static bool IsAuxiliaryCatalogEntry(string? name) => Regex.IsMatch(name ?? "",
        @"^Call of Duty[®™]?(?:\s|:).*\s[-–:]\s*(?:Multiplayer|OSX)(?:\s*\([^)]*\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool IsRuntime(int appId, string? name) => appId == 228980
        || Regex.IsMatch(name?.Trim() ?? "", @"^(?:Steam Linux Runtime(?:\s|$)|Steamworks (?:Common Redistributables|Shared|SDK)(?:\s|$)|SteamVR$|Proton(?:$|\s+(?:\d|Experimental\b|Hotfix\b|Next\b))|Microsoft (?:Visual C\+\+|\.NET .*Runtime)|DirectX (?:Runtime|Redistributable|Jun))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
