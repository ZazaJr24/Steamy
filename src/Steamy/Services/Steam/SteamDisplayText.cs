using System.Net;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>Store markup is displayed as text, never executed or embedded as HTML.</summary>
internal static class SteamDisplayText
{
    public static string Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = html[..Math.Min(html.Length, 200_000)];
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromSeconds(1);
        try
        {
            text = Regex.Replace(text, @"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", "", options, timeout);
            text = Regex.Replace(text, @"<br\s*/?>|</(?:p|div|h[1-6]|ul|ol|li)\s*>", "\n", options, timeout);
            text = Regex.Replace(text, @"<li\b[^>]*>", "\n• ", options, timeout);
            text = Regex.Replace(text, "<[^>]*>", "", options, timeout);
            text = Regex.Replace(text, @"\[img\][\s\S]*?\[/img\]", "", options, timeout);
            text = Regex.Replace(text, @"\[/?(?:p|h[1-6]|b|i|u|list|olist|\*|url|quote)(?:=[^\]]*)?\]", "", options, timeout);
            text = WebUtility.HtmlDecode(text).Replace("\r", "");
            text = Regex.Replace(text, @"[^\S\n]+", " ", options, timeout);
            text = Regex.Replace(text, @" *\n *", "\n", options, timeout);
            text = Regex.Replace(text, @"\n{3,}", "\n\n", options, timeout).Trim();
            return text[..Math.Min(text.Length, 32_000)];
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }
}
