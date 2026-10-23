using System.Text.RegularExpressions;

namespace Steamy.Services;

internal static class GameClassification
{
    // Legacy Call of Duty installs expose their multiplayer/OSX companion as
    // separate Steam apps classified "game". They are not another Windows base game.
    public static bool IsAuxiliaryCatalogEntry(string? name) => Regex.IsMatch(name ?? "",
        @"^Call of Duty[®™]?(?:\s|:).*\s[-–:]\s*(?:Multiplayer|OSX)(?:\s*\([^)]*\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool IsRuntime(int appId, string? name) => appId == 228980
        || Regex.IsMatch(name?.Trim() ?? "", @"^(?:Steam Linux Runtime(?:\s|$)|Steamworks (?:Common Redistributables|Shared|SDK)(?:\s|$)|SteamVR$|Proton(?:$|\s+(?:\d|Experimental\b|Hotfix\b|Next\b))|Microsoft (?:Visual C\+\+|\.NET .*Runtime)|DirectX (?:Runtime|Redistributable|Jun))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
