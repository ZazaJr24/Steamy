using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Models;

public enum SteamCatalogAppType
{
    Unknown,
    Game,
    Dlc,
    Software,
    Video,
    Hardware,
    Music
}

/// <summary>
/// Public Steam catalog metadata. It is deliberately separate from <see cref="Game"/>, which
/// represents an app installed in the user's local Steam library.
/// </summary>
public sealed class SteamCatalogItem : UiObservableObject
{
    public SteamCatalogItem()
    {
        Screenshots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScreenshots));
    }

    private string _shortDescription = string.Empty;
    private string _developersDisplay = string.Empty;
    private string _publishersDisplay = string.Empty;
    private string _releaseDate = string.Empty;
    private string _priceLabel = "Price unavailable";
    private string _genresDisplay = string.Empty;
    private string _systemRequirements = "System requirements unavailable";
    private string _storeUrl = string.Empty;
    private string _detailStatus = "Details not loaded";
    private bool _isFreeToPlay;
    private bool _isDetailsLoading;
    private bool _hasDetails;
    private DateTimeOffset? _detailsUpdatedAt;
    private bool _isArtworkLoading;
    private bool _screenshotsLoaded;
    private bool _isScreenshotsLoading;
    private string _screenshotStatus = "Screenshots not loaded";
    private System.Windows.Media.Imaging.BitmapImage? _artworkImage;
    private System.Windows.Media.Imaging.BitmapImage? _headerImage;
    private SteamCatalogAppType _appType;
    private bool _isInstalled;

    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public SteamCatalogAppType AppType
    {
        get => _appType;
        set
        {
            if (SetProperty(ref _appType, value)) OnPropertyChanged(nameof(TypeLabel));
        }
    }

    private bool _nsfw;

    public bool Nsfw
    {
        get => _nsfw;
        set => SetProperty(ref _nsfw, value);
    }
    public string HeaderImageUrl { get; set; } = string.Empty;
    public string CapsuleImageUrl { get; set; } = string.Empty;
    public string PortraitImageUrl { get; set; } = string.Empty;
    public string LibraryImageUrl { get; set; } = string.Empty;
    public ObservableCollection<SteamCatalogScreenshot> Screenshots { get; } = new();

    public string TypeLabel => AppType switch
    {
        SteamCatalogAppType.Game => "Game",
        SteamCatalogAppType.Dlc => "DLC",
        SteamCatalogAppType.Software => "Software",
        SteamCatalogAppType.Video => "Video",
        SteamCatalogAppType.Hardware => "Hardware",
        SteamCatalogAppType.Music => "Music",
        _ => "Game"
    };

    public string AppLabel => $"App {AppId}";
    public string InstallStateLabel => IsInstalled ? "Installed locally" : "Public catalog";
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value)) OnPropertyChanged(nameof(InstallStateLabel));
        }
    }

    public string ShortDescription
    {
        get => _shortDescription;
        set => SetProperty(ref _shortDescription, value);
    }

    public string DevelopersDisplay
    {
        get => _developersDisplay;
        set => SetProperty(ref _developersDisplay, value);
    }

    public string PublishersDisplay
    {
        get => _publishersDisplay;
        set => SetProperty(ref _publishersDisplay, value);
    }

    public string ReleaseDate
    {
        get => _releaseDate;
        set => SetProperty(ref _releaseDate, value);
    }

    public string PriceLabel
    {
        get => _priceLabel;
        set => SetProperty(ref _priceLabel, value);
    }

    public string GenresDisplay
    {
        get => _genresDisplay;
        set => SetProperty(ref _genresDisplay, value);
    }

    public string SystemRequirements
    {
        get => _systemRequirements;
        set => SetProperty(ref _systemRequirements, value);
    }

    public string StoreUrl
    {
        get => _storeUrl;
        set => SetProperty(ref _storeUrl, value);
    }

    public bool IsFreeToPlay
    {
        get => _isFreeToPlay;
        set => SetProperty(ref _isFreeToPlay, value);
    }

    public bool IsDetailsLoading
    {
        get => _isDetailsLoading;
        set => SetProperty(ref _isDetailsLoading, value);
    }

    public bool HasDetails
    {
        get => _hasDetails;
        set => SetProperty(ref _hasDetails, value);
    }

    public DateTimeOffset? DetailsUpdatedAt
    {
        get => _detailsUpdatedAt;
        set => SetProperty(ref _detailsUpdatedAt, value);
    }

    public string DetailStatus
    {
        get => _detailStatus;
        set => SetProperty(ref _detailStatus, value);
    }

    public bool IsArtworkLoading
    {
        get => _isArtworkLoading;
        set => SetProperty(ref _isArtworkLoading, value);
    }

    public bool ScreenshotsLoaded
    {
        get => _screenshotsLoaded;
        set => SetProperty(ref _screenshotsLoaded, value);
    }

    public bool IsScreenshotsLoading
    {
        get => _isScreenshotsLoading;
        set => SetProperty(ref _isScreenshotsLoading, value);
    }

    public string ScreenshotStatus
    {
        get => _screenshotStatus;
        set => SetProperty(ref _screenshotStatus, value);
    }

    public bool HasScreenshots => Screenshots.Count > 0;

    public string ScreenshotCountLabel => Screenshots.Count == 0
        ? "No screenshots"
        : $"{Screenshots.Count} screenshot(s)";

    public System.Windows.Media.Imaging.BitmapImage? ArtworkImage
    {
        get => _artworkImage;
        set
        {
            if (SetProperty(ref _artworkImage, value)) OnPropertyChanged(nameof(IsArtworkFallback));
        }
    }

    public System.Windows.Media.Imaging.BitmapImage? HeaderImage
    {
        get => _headerImage;
        set
        {
            if (SetProperty(ref _headerImage, value)) OnPropertyChanged(nameof(IsHeaderFallback));
        }
    }

    public bool IsArtworkFallback => ArtworkImage is null;
    public bool IsHeaderFallback => HeaderImage is null;
}

public sealed class SteamCatalogScreenshot : ObservableObject
{
    private System.Windows.Media.Imaging.BitmapImage? _image;

    public string ThumbnailUrl { get; init; } = string.Empty;
    public string FullUrl { get; init; } = string.Empty;

    public System.Windows.Media.Imaging.BitmapImage? Image
    {
        get => _image;
        set
        {
            if (SetProperty(ref _image, value)) OnPropertyChanged(nameof(IsFallback));
        }
    }

    public bool IsFallback => Image is null;
}

public sealed record SteamCatalogScreenshotInfo(string ThumbnailUrl, string FullUrl);

public sealed record SteamCatalogSnapshot(
    bool Succeeded,
    IReadOnlyList<SteamCatalogItem> Items,
    DateTimeOffset UpdatedAt,
    bool FromCache,
    string Message)
{
    public static SteamCatalogSnapshot Failure(string message) =>
        new(false, Array.Empty<SteamCatalogItem>(), DateTimeOffset.MinValue, false, message);
}

public sealed record SteamCatalogDetails(
    SteamCatalogAppType AppType,
    string HeaderImageUrl,
    string CapsuleImageUrl,
    string PortraitImageUrl,
    string LibraryImageUrl,
    string ShortDescription,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    string ReleaseDate,
    string PriceLabel,
    bool IsFreeToPlay,
    string GenresDisplay,
    IReadOnlyList<SteamCatalogScreenshotInfo> Screenshots,
    string SystemRequirements,
    string StoreUrl);

public static class SteamCatalogQuery
{
    public static IReadOnlyList<SteamCatalogItem> FilterAndSort(
        IEnumerable<SteamCatalogItem> items,
        string? searchText,
        string? typeFilter,
        string? sortOption,
        LibraryNsfwScope nsfwScope = LibraryNsfwScope.Hide)
    {
        ArgumentNullException.ThrowIfNull(items);

        var search = searchText?.Trim() ?? string.Empty;
        IEnumerable<SteamCatalogItem> query = items;
        if (search.Length > 0)
        {            query = query.Where(item =>
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.AppId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (nsfwScope == LibraryNsfwScope.Hide)
            query = query.Where(item => !item.Nsfw);

        query = typeFilter?.Trim() switch
        {
            "Game" or "Games" => query.Where(item => item.AppType == SteamCatalogAppType.Game),
            "DLC" or "Dlc" => query.Where(item => item.AppType == SteamCatalogAppType.Dlc),
            "Software" => query.Where(item => item.AppType == SteamCatalogAppType.Software),
            "Video" => query.Where(item => item.AppType == SteamCatalogAppType.Video),
            "Hardware" => query.Where(item => item.AppType == SteamCatalogAppType.Hardware),
            "Music" => query.Where(item => item.AppType == SteamCatalogAppType.Music),
            "Unknown" => query.Where(item => item.AppType == SteamCatalogAppType.Unknown),
            _ => query
        };

        return sortOption?.Trim() switch
        {
            "Popular (AAA)" => query
                .OrderByDescending(item => PopularityLookup.GetScore(item.AppId))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            "App ID" => query.OrderByDescending(item => item.AppId).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Type" => query.OrderBy(item => item.TypeLabel, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Installed first" => query.OrderByDescending(item => item.IsInstalled).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Name A–Z" => query.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.AppId).ToArray(),
            _ => query
                .OrderByDescending(item => PopularityLookup.GetScore(item.AppId))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
}

public static class PopularityLookup
{
    private static readonly Dictionary<int, int> Scores = new()
    {
        [1086940] = 98, // Baldur's Gate 3
        [1245620] = 97, // Elden Ring
        [1174180] = 96, // Red Dead Redemption 2
        [1091500] = 95, // Cyberpunk 2077
        [271590] = 94,  // Grand Theft Auto V
        [814380] = 93,  // Sekiro
        [1151640] = 92, // Horizon Zero Dawn
        [1593500] = 91, // God of War
        [1328670] = 90, // Mass Effect LE
        [374320] = 89,  // Dark Souls III
        [292030] = 88,  // The Witcher 3
        [1938090] = 87, // CoD MW III
        [1240440] = 86, // Halo Infinite
        [1446780] = 85, // Monster Hunter Rise
        [1817070] = 84, // Spider-Man Remastered
        [1817190] = 83, // Spider-Man Miles Morales
        [2050650] = 82, // Resident Evil 4
        [1196590] = 81, // Dragon's Dogma 2
        [553850] = 80,  // Helldivers 2
        [730] = 79,     // Counter-Strike 2
        [1172470] = 78, // Apex Legends
        [2358720] = 77, // Black Myth Wukong
        [1222670] = 76, // The Sims 4
        [570] = 75,     // Dota 2
        [440] = 74,     // Team Fortress 2
        [578080] = 73,  // PUBG
        [252490] = 72,  // Rust
        [346110] = 71,  // ARK
        [1174370] = 70, // Star Wars Jedi
    };

    public static int GetScore(int appId) => Scores.GetValueOrDefault(appId, 0);
}

public static class SteamCatalogPaging
{
    public static int TotalPages(int itemCount, int pageSize)
    {
        if (itemCount <= 0 || pageSize <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling(itemCount / (double)pageSize));
    }

    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var safePage = Math.Clamp(page, 1, TotalPages(items.Count, pageSize));
        return items.Skip((safePage - 1) * pageSize).Take(pageSize).ToArray();
    }
}

/// <summary>
/// Remote Ryuu generator feed entry for a single game's available fixes.
/// Mirrors <c>&lt;base&gt;/files/fixes.json</c> as fetched from <c>generator.ryuu.lol</c>.
/// It is feed metadata only; local workflow state lives in <see cref="GameFixItem"/>.
/// </summary>
public sealed class RyuuFixGame
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<RyuuFixEntry> Fixes { get; init; } = Array.Empty<RyuuFixEntry>();
}

public sealed class RyuuFixEntry
{
    public string Href { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public IReadOnlyList<string> Badges { get; init; } = Array.Empty<string>();
}
