using System.Text.RegularExpressions;

namespace Steamy.Services;

internal static class GameClassification
{
    public static bool IsRuntime(int appId, string? name) => appId == 228980
        || Regex.IsMatch(name?.Trim() ?? "", @"^(?:Steam Linux Runtime(?:\s|$)|Steamworks (?:Common Redistributables|Shared|SDK)(?:\s|$)|SteamVR$|Proton(?:$|\s+(?:\d|Experimental\b|Hotfix\b|Next\b))|Microsoft (?:Visual C\+\+|\.NET .*Runtime)|DirectX (?:Runtime|Redistributable|Jun))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
