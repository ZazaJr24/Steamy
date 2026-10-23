using System.Reflection;
using System.Windows;

namespace Steamy.Services;

/// <summary>Discord is excluded from the normal build; the archived integration can be rebuilt explicitly.</summary>
public static class DiscordIntegration
{
    private static readonly bool Included = bool.TryParse(typeof(DiscordIntegration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "DiscordIntegrationEnabled")?.Value, out var value) && value;
    internal static bool? TestOverride { get; set; }
    public static bool Enabled => TestOverride ?? Included;
    public static Visibility Visibility => Enabled ? Visibility.Visible : Visibility.Collapsed;
    public static GridLength TextColumnWidth => new(Enabled ? 1 : 0, Enabled ? GridUnitType.Star : GridUnitType.Pixel);
    public static GridLength IconColumnWidth => new(Enabled ? 36 : 0);
    internal static bool IsSocialPage(Type page) => page == typeof(Pages.FriendsPage) || page == typeof(Pages.FriendsHubPage) || page == typeof(Pages.CommunityPage);
}
