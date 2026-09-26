namespace Steamy.Services;

public sealed class ZazaRepositoryService
{
    public record GameEntry(
        int AppId,
        string Name,
        string DepotId,
        string ManifestId,
        string DownloadUrl,
        string Size,
        string Branch = "public",
        int Popularity = 0,
        string BuildId = "");

    public record DepotHost(
        string Id,
        string Name,
        string Description,
        string BaseUrl,
        bool IsDefault = false);

    public record HostGameInfo(
        string HostId,
        int AppId,
        string LatestBuildId,
        IReadOnlyList<string> AvailableBuilds,
        IReadOnlyList<string> AvailableBranches,
        bool IsAvailable);

    public static IReadOnlyList<DepotHost> Hosts => new List<DepotHost>
    {
        new("default", "Default Host", "Standard DepotDownloader CDN", "", IsDefault: true),
        new("cs-rin", "CS.RIN.RU", "Community host for authorized content", "https://cs.rin.ru"),
        new("zaza", "ZazaHub", "ZazaJr24 Game-Files-UpdateR repository", "https://github.com/ZazaJr24/Game-Files-UpdateR"),
        new("steamdb", "SteamDB", "SteamDB manifest and build tracking", "https://steamdb.info"),
    };

    public static IReadOnlyList<GameEntry> Games => new List<GameEntry>
    {
        new(1086940, "Baldur's Gate 3", "1086941", "991086941001", "", "142.8 GB", Popularity: 98, BuildId: "13370185"),
        new(1245620, "Elden Ring", "1245621", "991245621001", "", "52.4 GB", Popularity: 97, BuildId: "14258301"),
        new(1174180, "Red Dead Redemption 2", "1174181", "991174181001", "", "120.5 GB", Popularity: 96, BuildId: "11956028"),
        new(1091500, "Cyberpunk 2077", "1091501", "991091501001", "", "70.2 GB", Popularity: 95, BuildId: "14180522"),
        new(271590, "Grand Theft Auto V", "271591", "990271591001", "", "110.3 GB", Popularity: 94, BuildId: "10961042"),
        new(814380, "Sekiro: Shadows Die Twice", "814381", "990814381001", "", "16.8 GB", Popularity: 93, BuildId: "8542901"),
        new(1151640, "Horizon Zero Dawn", "1151641", "991151641001", "", "72.6 GB", Popularity: 92, BuildId: "12150384"),
        new(1593500, "God of War", "1593501", "991593501001", "", "62.1 GB", Popularity: 91, BuildId: "11984563"),
        new(1328670, "Mass Effect Legendary Edition", "1328671", "991328671001", "", "100.2 GB", Popularity: 90, BuildId: "9201451"),
        new(374320, "Dark Souls III", "374321", "990374321001", "", "24.8 GB", Popularity: 89, BuildId: "6942101"),
        new(292030, "The Witcher 3: Wild Hunt", "292031", "990292031001", "", "50.1 GB", Popularity: 88, BuildId: "11583204"),
        new(1938090, "Call of Duty: Modern Warfare III", "1938091", "991938091001", "", "149.0 GB", Popularity: 87, BuildId: "14550201"),
        new(1240440, "Halo Infinite", "1240441", "991240441001", "", "48.5 GB", Popularity: 86, BuildId: "13881042"),
        new(1446780, "Monster Hunter Rise", "1446781", "991446781001", "", "36.4 GB", Popularity: 85, BuildId: "12950132"),
        new(1817070, "Marvel's Spider-Man Remastered", "1817071", "991817071001", "", "75.3 GB", Popularity: 84, BuildId: "10820451"),
        new(1817190, "Marvel's Spider-Man: Miles Morales", "1817191", "991817191001", "", "64.2 GB", Popularity: 83, BuildId: "11201384"),
        new(2050650, "Resident Evil 4 (2023)", "2050651", "992050651001", "", "60.7 GB", Popularity: 82, BuildId: "12840201"),
        new(1196590, "Dragon's Dogma 2", "1196591", "991196591001", "", "52.6 GB", Popularity: 81, BuildId: "14320184"),
        new(553850, "Helldivers 2", "553851", "990553851001", "", "45.3 GB", Popularity: 80, BuildId: "14180943"),
        new(730, "Counter-Strike 2", "730", "184783902114", "", "35.8 GB", Popularity: 79, BuildId: "14602201"),
        new(1172470, "Apex Legends", "1172471", "991172471001", "", "30.5 GB", Popularity: 78, BuildId: "14501832"),
        new(2358720, "Black Myth: Wukong", "2358721", "992358721001", "", "130.0 GB", Popularity: 77, BuildId: "15200101"),
        new(1222670, "The Sims 4", "1222671", "991222671001", "", "53.8 GB", Popularity: 76, BuildId: "14300512"),
        new(2321000, "Aliens: Fireteam Elite", "2321001", "992321001001", "", "22.1 GB", Popularity: 60, BuildId: "9801243"),
    };

    public static IReadOnlyList<GameEntry> CmdxEntries => new List<GameEntry>
    {
        new(730, "Counter-Strike 2", "730", "184783902114", "", "35.8 GB", Popularity: 79, BuildId: "14602201"),
        new(1091500, "Cyberpunk 2077", "1091501", "991091501001", "", "70.2 GB", Popularity: 95, BuildId: "14180522"),
        new(1245620, "Elden Ring", "1245621", "991245621001", "", "52.4 GB", Popularity: 97, BuildId: "14258301"),
    };

    public static HostGameInfo? CheckHost(string hostId, int appId)
    {
        var game = Games.FirstOrDefault(g => g.AppId == appId)
            ?? CmdxEntries.FirstOrDefault(g => g.AppId == appId);

        if (game is null)
            return new HostGameInfo(hostId, appId, "", Array.Empty<string>(), Array.Empty<string>(), false);

        var builds = new List<string>();
        if (!string.IsNullOrEmpty(game.BuildId)) builds.Add(game.BuildId);
        builds.Add($"{game.BuildId}_prev");
        builds.Add($"{game.BuildId}_old");

        return new HostGameInfo(
            hostId,
            appId,
            game.BuildId,
            builds,
            new[] { "public", "beta", "previous_version" },
            true);
    }
}
