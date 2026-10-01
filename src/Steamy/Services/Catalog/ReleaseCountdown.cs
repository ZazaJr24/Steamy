using System.Globalization;
using System.Text.RegularExpressions;

namespace Steamy.Services;

public static class ReleaseCountdown
{
    public static string Label(SpotlightGame game, DateTimeOffset now)
    {
        if (game.ReleaseTime is { } time)
        {
            var remaining = time - now;
            return remaining <= TimeSpan.Zero ? "Awaiting release confirmation"
                : $"{(int)remaining.TotalDays}d {remaining.Hours:00}h {remaining.Minutes:00}m {remaining.Seconds:00}s";
        }
        if (game.ReleaseDate is not { } date) return "TBA";
        var days = date.DayNumber - DateOnly.FromDateTime(now.DateTime).DayNumber;
        return days < 0 ? "Awaiting release confirmation" : days == 0 ? "Today" : $"{days:N0} {(days == 1 ? "day" : "days")}";
    }

    public static IReadOnlyList<SpotlightGame> Upcoming(IEnumerable<SpotlightGame> games, DateOnly today) => games
        .Where(game => game.ComingSoon && !Expired(game, today))
        .OrderBy(game => game.ReleaseDate is null)
        .ThenBy(game => game.ReleaseDate)
        .ToArray();

    private static bool Expired(SpotlightGame game, DateOnly today)
    {
        if (game.ReleaseDate is { } day) return day < today;
        if (DateTime.TryParseExact(game.ReleaseLabel, ["MMMM yyyy", "MMM yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var month)) return month.AddMonths(1).Date <= today.ToDateTime(TimeOnly.MinValue);
        var yearMatch = Regex.Match(game.ReleaseLabel, @"\b(20\d{2})\b");
        if (yearMatch.Success && int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture) < today.Year) return true;
        var quarter = Regex.Match(game.ReleaseLabel, @"\bQ([1-4])\s+(20\d{2})\b", RegexOptions.IgnoreCase);
        if (quarter.Success && int.Parse(quarter.Groups[2].Value, CultureInfo.InvariantCulture) == today.Year
            && int.Parse(quarter.Groups[1].Value, CultureInfo.InvariantCulture) < (today.Month - 1) / 3 + 1) return true;
        return int.TryParse(game.ReleaseLabel, out var year) && year is >= 2000 and <= 2200 && year < today.Year;
    }
}
