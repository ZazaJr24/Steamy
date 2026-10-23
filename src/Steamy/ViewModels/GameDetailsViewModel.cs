using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Playtime;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class GameDetailsViewModel : ViewModelBase
{
    private readonly IArtworkService _artwork;
    private readonly IGameDetailsDataService _data;
    private readonly ISteamCatalogService? _catalog;
    private readonly IGamePreferencesStore _preferences;
    private readonly IGameLaunchService _launcher;
    private readonly IPlayTimeService _playtime;
    private readonly IGameActivityService _activity;
    private readonly ISecureCredentialService _credentials;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _saveDelay;
    private CancellationTokenSource? _timeLoad;
    private Task _lastSave = Task.CompletedTask;
    private bool _preferencesLoaded, _dirty, _refreshingTime;
    private int _revision, _editRevision, _achievementRevision, _timeRevision = -1, _timeRequest;
    private Game? _game;
    private WeakReference<ImageSource>? _hero, _logo;
    private string _description = "", _developer = "", _publisher = "", _notes = "", _arguments = "", _executable = "", _lastPlayedCaption = "Last played";
    private string _saveStatus = "", _launchStatus = "", _playTime = "0m", _lastPlayed = "Not played yet", _achievementStatus = "Loading achievements…";
    private bool _isPlaying, _achievementsAvailable, _achievementsBusy;
    private int _selectedTab;
    private bool _returnToLibrary;
    private Action? _restoreDashboard;
    private readonly HashSet<DownloadJob> _watchedDownloads = [];
    private bool _watchingDownloads;
    private DownloadJob? _download;
    public DownloadJob? Download => _download;
    public bool HasDownload => _download is not null;
    public string InstallLabel => _download is null ? "Install" : _download.IsActive ? "Downloading" : _download.StateLabel;
    public void OpenDownloads() => Navigation.Navigate<DownloadsPage>();
    private void WatchDownloads()
    {
        if (!_watchingDownloads) { Store.Downloads.CollectionChanged += DownloadsChanged; Store.Games.CollectionChanged += GamesChanged; _watchingDownloads = true; }
        var present = Store.Downloads.ToHashSet();
        foreach (var job in _watchedDownloads.Where(job => !present.Contains(job)).ToArray())
        { job.PropertyChanged -= DownloadChanged; _watchedDownloads.Remove(job); }
        foreach (var job in present) if (_watchedDownloads.Add(job)) job.PropertyChanged += DownloadChanged;
        RefreshDownload();
    }
    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs args) => WatchDownloads();
    private void GamesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshDownload();
    private void DownloadChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(DownloadJob.State) or nameof(DownloadJob.Priority) or nameof(DownloadJob.QueuePosition)) RefreshDownload(); }
    private void NotifyInstallation()
    {
        foreach (var name in new[] { nameof(Name), nameof(IsInstalled), nameof(CanInstall), nameof(SourceCaption), nameof(SourceLabel), nameof(InstallationLabel), nameof(LaunchTarget), nameof(IsLocalGame) }) OnPropertyChanged(name);
        ((AsyncRelayCommand)PlayCommand).NotifyCanExecuteChanged(); ((RelayCommand)OpenFolderCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddToSteam)); NotifySteamShortcutCommands();
    }
    private void RefreshDownload()
    {
        var matching = Store.Downloads.Where(job => Game?.AppId > 0 && job.AppId == Game.AppId && !job.IsTerminal).ToArray();
        _download = matching.FirstOrDefault(job => job.IsActive) ?? matching.FirstOrDefault(job => job.State == DownloadJobState.Paused)
            ?? matching.OrderByDescending(job => job.Priority).ThenBy(job => job.QueuePosition).FirstOrDefault();
        OnPropertyChanged(nameof(Download)); OnPropertyChanged(nameof(HasDownload)); OnPropertyChanged(nameof(InstallLabel));
        var libraryGame = Store.Games.FirstOrDefault(game => Game?.AppId > 0 ? game.AppId == Game.AppId : ReferenceEquals(game, Game));
        if (libraryGame is not null && !ReferenceEquals(libraryGame, Game))
        {
            Game = libraryGame;
            NotifyInstallation();
        }
    }
    private string _fullDescription = "", _releaseDate = "", _genres = "", _requirements = "", _languages = "", _features = "", _infoStatus = "";
    public GameDetailsViewModel(IAppDataStore store, INavigationService navigation, ILoggingService logging,
        IArtworkService artwork, IGameDetailsDataService data, IGamePreferencesStore preferences, IGameLaunchService launcher,
        IPlayTimeService playtime, IGameActivityService activity, ISecureCredentialService credentials, ISteamCatalogService? catalog = null) : base(store, navigation, logging)
    {
        _artwork = artwork; _data = data; _preferences = preferences; _launcher = launcher; _catalog = catalog;
        _playtime = playtime; _activity = activity; _credentials = credentials;
        PlayCommand = new AsyncRelayCommand(LaunchAsync, () => IsInstalled && !IsPlaying);
        AddToSteamCommand = new RelayCommand(RequestAddToSteam, () => CanAddToSteam);
        SaveCommand = new AsyncRelayCommand(FlushAsync);
        RefreshAchievementsCommand = new AsyncRelayCommand(RefreshAchievementsAsync, () => Game is not null && !AchievementsBusy);
        BackCommand = new RelayCommand(() =>
        {
            if (_returnToLibrary) Navigation.Navigate<LibraryPage>();
            else { _restoreDashboard?.Invoke(); Navigation.Navigate<DashboardPage>(); }
        });
        OpenFolderCommand = new RelayCommand(() => OpenExternal(Game?.InstallFolder), () => !string.IsNullOrWhiteSpace(Game?.InstallFolder));
        OpenSteamCommand = new RelayCommand(() => OpenExternal($"steam://nav/games/details/{Game?.AppId}"), () => Game?.AppId > 0);
        OpenCommunityCommand = new RelayCommand(() => OpenExternal($"https://steamcommunity.com/app/{Game?.AppId}"), () => Game?.AppId > 0);
        OpenSettingsCommand = new RelayCommand(() => Navigation.Navigate<SettingsPage>());
        SelectTabCommand = new RelayCommand<string>(value => { if (int.TryParse(value, out var index)) SelectedTab = index; });
        OpenRelatedGameCommand = new AsyncRelayCommand<SteamCatalogItem>(async item =>
        {
            if (item is null || item.AppId == Game?.AppId) return;
            await FlushAsync();
            var installed = Store.Games.FirstOrDefault(game => game.AppId == item.AppId && game.InstallState == GameInstallState.Installed);
            if (installed is not null) SelectGame(installed, returnToLibrary: true); else SelectCatalogGame(item);
            // This page is already displayed. Navigating to the same cached page
            // does not raise Loaded again, so refresh the newly selected game here.
            try { await ActivateAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException)
            { Logging.Add(LogLevel.Warning, "Library", "Some game details could not be loaded."); }
        });
    }
    public Game? Game { get => _game; private set => SetProperty(ref _game, value); }
    public string Name => Game?.Name ?? "Your game";
    public bool IsInstalled => Game?.InstallState == GameInstallState.Installed;
    public bool CanInstall => Game?.AppId > 0 && !IsInstalled;
    public SteamCatalogItem? CatalogGame { get; private set; }
    public string SourceCaption => IsInstalled ? "INSTALLED FROM" : "INSTALL WITH";
    public string SourceLabel => Game is null ? "" : !IsInstalled ? "DepotDownloader · Steam" : Game.InstallationSource.Length > 0 ? Game.InstallationSource : Game.IsLocalDownload ? "Local installation" : "Steam";
    public string InstallationLabel => !IsInstalled ? "Choose a folder and download source with Install." : Game?.InstallFolder ?? "";
    public string LaunchTarget => Game is null ? "" : Game.IsLocalDownload ? (LaunchExecutable.Length > 0 ? LaunchExecutable : Game.LaunchExecutable.Length > 0 ? Game.LaunchExecutable : "No executable detected") : "Launch with Steam";
    public bool IsLocalGame => Game?.IsLocalDownload == true;
    public bool CanAddToSteam => IsInstalled;
    public event Action<SteamCatalogItem>? AddToSteamRequested;
    private void NotifySteamShortcutCommands() => ((RelayCommand)AddToSteamCommand).NotifyCanExecuteChanged();
    public string LaunchExecutable { get => _executable; private set { if (SetProperty(ref _executable, value)) { OnPropertyChanged(nameof(LaunchTarget)); Edited(); } } }
    public string LastPlayedCaption { get => _lastPlayedCaption; private set => SetProperty(ref _lastPlayedCaption, value); }
    public bool IsSteamGame => Game?.AppId > 0;
    public ImageSource? HeroArtwork
    {
        get => _hero?.TryGetTarget(out var image) == true ? image : null;
        private set { if (ReferenceEquals(HeroArtwork, value)) return; _hero = value is null ? null : new(value); OnPropertyChanged(); }
    }
    public ImageSource? LogoArtwork
    {
        get => _logo?.TryGetTarget(out var image) == true ? image : null;
        private set { if (ReferenceEquals(LogoArtwork, value)) return; _logo = value is null ? null : new(value); OnPropertyChanged(); OnPropertyChanged(nameof(HasLogo)); }
    }
    public bool HasLogo => LogoArtwork is not null;
    public string Description { get => _description; private set => SetProperty(ref _description, value); }
    private GameReviewSummary? _reviews;
    public GameReviewSummary? Reviews { get => _reviews; private set { if (SetProperty(ref _reviews, value)) foreach (var name in new[] { nameof(ReviewPercentLabel), nameof(ReviewDescription), nameof(ReviewCountLabel), nameof(HasReviews) }) OnPropertyChanged(name); } }
    public bool HasReviews => Reviews is { Total: > 0 };
    public string ReviewPercentLabel => HasReviews ? $"{Reviews!.PositivePercent:0}%" : "—";
    public string ReviewDescription => HasReviews ? Reviews!.Description : "No rating available";
    public string ReviewCountLabel => HasReviews ? $"{Reviews!.Total:N0} Steam reviews" : "Steam player reviews";
    public string Developer { get => _developer; private set { if (SetProperty(ref _developer, value)) foreach (var name in new[] { nameof(HasDeveloper), nameof(MoreDeveloperHeading), nameof(DeveloperStoreUri) }) OnPropertyChanged(name); } }
    public bool HasDeveloper => !string.IsNullOrWhiteSpace(Developer) && Developer != "Not available";
    public string MoreDeveloperHeading => "More from " + Developer;
    private string _developerStoreName = "";
    public Uri? DeveloperStoreUri => HasDeveloper ? new Uri("https://store.steampowered.com/search/?developer=" + Uri.EscapeDataString(_developerStoreName.Length > 0 ? _developerStoreName : Developer)) : null;
    public string Publisher { get => _publisher; private set => SetProperty(ref _publisher, value); }
    public string FullDescription { get => _fullDescription; private set { if (SetProperty(ref _fullDescription, value)) OnPropertyChanged(nameof(HasFullDescription)); } }
    public string ReleaseDate { get => _releaseDate; private set => SetProperty(ref _releaseDate, value); }
    public string Genres { get => _genres; private set => SetProperty(ref _genres, value); }
    public string SystemRequirements { get => _requirements; private set { if (SetProperty(ref _requirements, value)) OnPropertyChanged(nameof(HasRequirements)); } }
    public string Languages { get => _languages; private set => SetProperty(ref _languages, value); }
    public string Features { get => _features; private set => SetProperty(ref _features, value); }
    public string InfoStatus { get => _infoStatus; private set => SetProperty(ref _infoStatus, value); }
    public ObservableCollection<GameScreenshot> Screenshots { get; } = new();
    public ObservableCollection<GameNews> News { get; } = new();
    public bool HasScreenshots => Screenshots.Count > 0;
    public bool HasNews => News.Count > 0;
    public bool HasFullDescription => FullDescription.Length > 0;
    public bool HasRequirements => SystemRequirements.Length > 0;
    public string Notes { get => _notes; set { if (SetProperty(ref _notes, value)) Edited(); } }
    public bool CanEditPreferences => _preferencesLoaded;
    public string LaunchArguments { get => _arguments; set { if (SetProperty(ref _arguments, value)) Edited(); } }
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public string LaunchStatus { get => _launchStatus; private set => SetProperty(ref _launchStatus, value); }
    public string PlayTimeLabel { get => _playTime; private set => SetProperty(ref _playTime, value); }
    public string LastPlayedLabel { get => _lastPlayed; private set => SetProperty(ref _lastPlayed, value); }
    public bool IsPlaying { get => _isPlaying; private set { if (SetProperty(ref _isPlaying, value)) { ((AsyncRelayCommand)PlayCommand).NotifyCanExecuteChanged(); OnPropertyChanged(nameof(PlayLabel)); } } }
    public string PlayLabel => IsPlaying ? "Running" : "Play";
    public string AchievementStatus { get => _achievementStatus; private set => SetProperty(ref _achievementStatus, value); }
    public bool AchievementsAvailable { get => _achievementsAvailable; private set => SetProperty(ref _achievementsAvailable, value); }
    public bool AchievementsBusy { get => _achievementsBusy; private set { if (SetProperty(ref _achievementsBusy, value)) ((AsyncRelayCommand)RefreshAchievementsCommand).NotifyCanExecuteChanged(); } }
    public ObservableCollection<GameAchievement> Achievements { get; } = new();
    public int UnlockedCount => Achievements.Count(item => item.Unlocked);
    public int AchievementCount => Achievements.Count;
    public double AchievementPercent => AchievementCount == 0 ? 0 : 100d * UnlockedCount / AchievementCount;
    public string AchievementSummary => AchievementsAvailable ? $"{UnlockedCount} / {AchievementCount} unlocked" : "Unavailable";
    public int SelectedTab { get => _selectedTab; set { if (SetProperty(ref _selectedTab, Math.Clamp(value, 0, 3))) foreach (var name in new[] { nameof(IsOverview), nameof(IsAchievements), nameof(IsNotes), nameof(IsLaunchOptions) }) OnPropertyChanged(name); } }
    public bool IsOverview => SelectedTab == 0;
    public bool IsAchievements => SelectedTab == 1;
    public bool IsNotes => SelectedTab == 2;
    public bool IsLaunchOptions => SelectedTab == 3;
    public ICommand PlayCommand { get; }
    public ICommand AddToSteamCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RefreshAchievementsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenSteamCommand { get; }
    public ICommand OpenCommunityCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand OpenRelatedGameCommand { get; }
    public ObservableCollection<SteamCatalogItem> DeveloperGames { get; } = [];
    public ObservableCollection<SteamCatalogItem> SimilarGames { get; } = [];
    public bool HasDeveloperGames => DeveloperGames.Count > 0;
    public bool HasSimilarGames => SimilarGames.Count > 0;
    public IEnumerable<GameAchievement> AchievementPreview => Achievements.Take(8);

    public void SelectGame(Game game, bool returnToLibrary = false, Action? restoreDashboard = null)
    {
        CatalogGame = null;
        _ = FlushAsync();
        _load?.Cancel(); _load?.Dispose(); _load = null;
        _revision++; _preferencesLoaded = false; _dirty = false;
        OnPropertyChanged(nameof(CanEditPreferences));
        Game = game; _returnToLibrary = returnToLibrary; _restoreDashboard = restoreDashboard;
        WatchDownloads();
        HeroArtwork = game.HeaderImage ?? game.ArtworkImage; LogoArtwork = null;
        Notes = ""; LaunchArguments = ""; LaunchExecutable = ""; SaveStatus = ""; LaunchStatus = ""; LastPlayedCaption = "Last played";
        Description = "Ready to play. Keep your notes and playtime together in one place."; Developer = "Not available"; Publisher = "Not available";
        FullDescription = ""; ReleaseDate = ""; Genres = ""; SystemRequirements = ""; Languages = ""; Features = ""; Reviews = null;
        _developerStoreName = ""; OnPropertyChanged(nameof(DeveloperStoreUri)); DeveloperGames.Clear(); SimilarGames.Clear(); OnPropertyChanged(nameof(HasDeveloperGames)); OnPropertyChanged(nameof(HasSimilarGames));
        Screenshots.Clear(); News.Clear(); OnPropertyChanged(nameof(HasScreenshots)); OnPropertyChanged(nameof(HasNews));
        InfoStatus = game.AppId > 0 ? "Loading game information from Steam…" : "No Steam ID matched for this installation. Local installation and launch details are shown below.";
        PlayTimeLabel = "0m"; LastPlayedLabel = "No launch recorded";
        IsPlaying = false; SelectedTab = 0; Achievements.Clear(); AchievementsAvailable = false; AchievementsBusy = false;
        AchievementStatus = "Loading achievements…";
        foreach (var name in new[] { nameof(Name), nameof(SourceLabel), nameof(InstallationLabel), nameof(LaunchTarget), nameof(IsSteamGame), nameof(IsLocalGame), nameof(IsInstalled), nameof(CanInstall), nameof(SourceCaption) }) OnPropertyChanged(name);
        RaiseAchievementStats();
        ((AsyncRelayCommand)PlayCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddToSteam)); NotifySteamShortcutCommands();
        ((RelayCommand)OpenFolderCommand).NotifyCanExecuteChanged();
        ((RelayCommand)OpenSteamCommand).NotifyCanExecuteChanged();
        ((RelayCommand)OpenCommunityCommand).NotifyCanExecuteChanged();
    }
    public void SelectCatalogGame(SteamCatalogItem item)
    {
        SelectGame(new Game { AppId = item.AppId, Name = item.Name, InstallState = GameInstallState.NotInstalled,
            ArtworkImage = item.ArtworkImage, HeaderImage = item.HeaderImage }, returnToLibrary: true);
        CatalogGame = item;
        Description = "Discover the game, then choose where and how to install it.";
    }
    public async Task ActivateAsync()
    {
        WatchDownloads();
        if (Game is not { } game) return;
        _load?.Cancel(); _load?.Dispose(); _load = new();
        var token = _load.Token;
        var revision = _revision;
        await Task.WhenAll(LoadPreferencesAsync(), LoadArtworkAsync(), LoadOverviewAsync(), RefreshPlayTimeAsync(), RefreshAchievementsAsync());

        async Task LoadPreferencesAsync()
        {
            try
            {
                await _lastSave.WaitAsync(token);
                if (revision != _revision || token.IsCancellationRequested) return;
                // A failed save is still the user's draft. Reopening the same
                // game must not replace it with an older on-disk value.
                if (_preferencesLoaded && _dirty) return;
                var edit = _editRevision;
                var value = await ReadBackgroundAsync(() => _preferences.ReadAsync(game.AppId, token), token);
                if (revision != _revision || token.IsCancellationRequested
                    || _preferencesLoaded && (_dirty || edit != _editRevision)) return;
                // Loading values is not an edit, including a reload of an
                // already editable game. New edits made during the read win.
                _preferencesLoaded = false;
                Notes = value.Notes; LaunchArguments = value.LaunchArguments; LaunchExecutable = value.LaunchExecutable; _preferencesLoaded = true;
                OnPropertyChanged(nameof(CanEditPreferences));
            }
            catch (OperationCanceledException) { }
        }
        async Task LoadArtworkAsync()
        {
            await Task.WhenAll(LoadHeroAsync(), LoadLogoAsync());

            async Task LoadHeroAsync()
            {
                try
                {
                    var hero = await _artwork.LoadHeroAsync(game.AppId, token);
                    if (revision != _revision || token.IsCancellationRequested) return;
                    HeroArtwork = hero ?? game.HeaderImage ?? game.ArtworkImage;
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException) { }
            }
            async Task LoadLogoAsync()
            {
                try
                {
                    var logo = await _artwork.LoadGameLogoAsync(game, token);
                    if (revision != _revision || token.IsCancellationRequested) return;
                    var trimmed = await Controls.GameLogoLayout.TrimAsync(logo, token);
                    if (revision != _revision || token.IsCancellationRequested) return;
                    LogoArtwork = trimmed;
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException) { }
            }
        }
        async Task LoadOverviewAsync()
        {
            var relatedRequested = false;
            try
            {
                await foreach (var overview in ReadOverviewUpdatesAsync(game.AppId, token))
                {
                    if (revision != _revision || token.IsCancellationRequested) return;
                    if (!string.IsNullOrWhiteSpace(overview.Description)) Description = System.Net.WebUtility.HtmlDecode(overview.Description);
                    if (_developerStoreName != overview.DeveloperStoreName)
                    { _developerStoreName = overview.DeveloperStoreName; OnPropertyChanged(nameof(DeveloperStoreUri)); }
                    if (!string.IsNullOrWhiteSpace(overview.Developer)) Developer = overview.Developer;
                    if (!string.IsNullOrWhiteSpace(overview.Publisher)) Publisher = overview.Publisher;
                    FullDescription = overview.DetailedDescription; Reviews = overview.Reviews;
                    ReleaseDate = overview.ReleaseDate; Genres = overview.Genres;
                    SystemRequirements = overview.SystemRequirements; Languages = overview.Languages; Features = overview.Features;
                    if (!Screenshots.SequenceEqual(overview.Screenshots.Take(4)))
                    { Screenshots.Clear(); foreach (var item in overview.Screenshots.Take(4)) Screenshots.Add(item); OnPropertyChanged(nameof(HasScreenshots)); }
                    if (!News.SequenceEqual(overview.News))
                    { News.Clear(); foreach (var item in overview.News) News.Add(item); OnPropertyChanged(nameof(HasNews)); }
                    if (!relatedRequested && (overview.Developer.Length > 0 || overview.Genres.Length > 0))
                    { relatedRequested = true; _ = LoadRelatedGamesAsync(game.AppId, overview, revision, token); }
                    if (game.AppId > 0) InfoStatus = string.IsNullOrWhiteSpace(overview.Description)
                        ? "Steam information is unavailable right now. Reopen this game to retry." : "Game information loaded automatically from Steam";
                }
            }
            catch (OperationCanceledException) { }
        }
    }
    private async IAsyncEnumerable<GameOverview> ReadOverviewUpdatesAsync(int appId, [EnumeratorCancellation] CancellationToken token)
    {
        if (_data is IProgressiveGameDetailsDataService progressive)
        {
            await foreach (var overview in progressive.ReadOverviewUpdatesAsync(appId, token)) yield return overview;
        }
        else yield return await _data.ReadOverviewAsync(appId, token);
    }
    private async Task LoadRelatedGamesAsync(int appId, GameOverview overview, int revision, CancellationToken token)
    {
        if (_catalog is null) return;
        try
        {
            var selected = await Task.Run(async () =>
            {
                var knownGames = await _catalog.GetRecommendationCandidatesAsync(token).WaitAsync(token).ConfigureAwait(false);
                return GameRelatedSelection.Select(knownGames, appId, overview, token);
            }, token);
            if (token.IsCancellationRequested || revision != _revision) return;
            var developerGames = selected.Developer;
            var similarGames = selected.Similar;
            DeveloperGames.Clear(); foreach (var item in developerGames) DeveloperGames.Add(item);
            SimilarGames.Clear(); foreach (var item in similarGames) SimilarGames.Add(item);
            OnPropertyChanged(nameof(HasDeveloperGames)); OnPropertyChanged(nameof(HasSimilarGames));
            await Task.WhenAll(developerGames.Concat(similarGames).Select(item => _catalog.EnsureArtworkAsync(item, cancellationToken: token)));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or HttpRequestException) { }
    }
    public async Task DeactivateAsync()
    {
        Store.Downloads.CollectionChanged -= DownloadsChanged; Store.Games.CollectionChanged -= GamesChanged; _watchingDownloads = false;
        foreach (var job in _watchedDownloads) job.PropertyChanged -= DownloadChanged;
        _watchedDownloads.Clear();
        _load?.Cancel();
        await FlushAsync();
    }
    public async Task RefreshPlayTimeAsync()
    {
        var load = _load;
        if (Game is not { } game || (_refreshingTime && _timeRevision == _revision && ReferenceEquals(_timeLoad, load))) return;
        _refreshingTime = true;
        _timeLoad = load;
        var token = load?.Token ?? default;
        var revision = _revision;
        _timeRevision = revision;
        var request = ++_timeRequest;
        try
        {
            var snapshot = await ReadBackgroundAsync(() => _playtime.ReadAsync(token), token);
            var activity = await ReadBackgroundAsync(() => _activity.GetAsync(token), token);
            var steamLastPlayed = await ReadBackgroundAsync(() => _playtime.ReadLastPlayedAsync(game.AppId, token), token);
            if (revision != _revision || token.IsCancellationRequested || !ReferenceEquals(load, _load)) return;
            var time = snapshot.Games.FirstOrDefault(item => item.AppId == game.AppId);
            PlayTimeLabel = PlayTimeFiles.Format(time?.Seconds ?? 0);
            IsPlaying = time?.IsRunning == true && DateTimeOffset.UtcNow - snapshot.UpdatedAt < TimeSpan.FromSeconds(20);
            var last = activity.Launches.FirstOrDefault(item => item.AppId == game.AppId)?.OpenedAt;
            LastPlayedCaption = "Last launch";
            if (steamLastPlayed is { } steamDate && (last is null || steamDate > last)) { last = steamDate; LastPlayedCaption = "Last played"; }
            if (last is { } date) LastPlayedLabel = date.LocalDateTime.ToString("g");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { if (request == _timeRequest) { _refreshingTime = false; _timeLoad = null; } }
    }

    private static async Task<T> ReadBackgroundAsync<T>(Func<Task<T>> read, CancellationToken token)
    {
        // File probes and provider setup can execute before their first await.
        // Keep them off the dispatcher and stop waiting as soon as this view leaves.
        var work = Task.Run(read, token);
        _ = work.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await work.WaitAsync(token).ConfigureAwait(false);
    }
    public async Task RefreshAchievementsAsync()
    {
        if (Game is not { } game) return;
        var revision = _revision;
        var request = ++_achievementRevision;
        var token = _load?.Token ?? default;
        AchievementsBusy = true;
        try
        {
            var snapshot = await _data.ReadAchievementsAsync(game.AppId, token);
            if (revision != _revision || request != _achievementRevision || token.IsCancellationRequested) return;
            Achievements.Clear(); foreach (var item in snapshot.Items) Achievements.Add(item);
            AchievementsAvailable = snapshot.Available; AchievementStatus = snapshot.Message; RaiseAchievementStats();
        }
        catch (OperationCanceledException) { }
        finally { if (revision == _revision && request == _achievementRevision) AchievementsBusy = false; }
    }
    public async Task ConnectAchievementsAsync(string key)
    {
        key = key.Trim();
        if (key.Length != 32 || !key.All(Uri.IsHexDigit)) { AchievementStatus = "Enter a valid 32-character Steam Web API key."; return; }
        try { await _credentials.SaveAsync(SteamGameDetailsService.CredentialKey, key.Trim()); await RefreshAchievementsAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { AchievementStatus = "The key could not be saved. Please try again."; }
    }
    private void RaiseAchievementStats() { foreach (var name in new[] { nameof(UnlockedCount), nameof(AchievementCount), nameof(AchievementPercent), nameof(AchievementSummary), nameof(AchievementPreview) }) OnPropertyChanged(name); }
    private async Task LaunchAsync()
    {
        if (Game is not { } game || !IsInstalled) return;
        var revision = _revision;
        await FlushAsync();
        if (_dirty) { LaunchStatus = "Save your launch options before starting the game."; return; }
        var result = await _launcher.LaunchAsync(game);
        if (revision != _revision) return;
        LaunchStatus = result.Message;
        if (result.Succeeded) await RefreshPlayTimeAsync();
    }
    private void RequestAddToSteam()
    {
        if (!CanAddToSteam || Game is not { } game) return;
        AddToSteamRequested?.Invoke(new SteamCatalogItem { AppId = game.AppId, Name = game.Name,
            ArtworkImage = game.ArtworkImage, HeaderImage = game.HeaderImage });
    }
    private void Edited()
    {
        if (!_preferencesLoaded) return;
        _dirty = true; _editRevision++; SaveStatus = "Saving…";
        _saveDelay?.Cancel(); _saveDelay?.Dispose(); _saveDelay = new();
        _ = DebounceAsync(_saveDelay.Token);
    }
    private async Task DebounceAsync(CancellationToken token)
    {
        try { await Task.Delay(500, token); await FlushAsync(); } catch (OperationCanceledException) { }
    }
    public Task FlushAsync()
    {
        _saveDelay?.Cancel();
        if (!_dirty || Game is not { } game) return _lastSave;
        _dirty = false;
        var preferences = new GamePreferences(Notes, LaunchArguments, LaunchExecutable);
        var previous = _lastSave;
        var revision = _revision; var edit = _editRevision;
        return _lastSave = PersistAsync();
        async Task PersistAsync()
        {
            try
            {
                await previous;
                // Saving owns its captured game/options and outlives page cancellation.
                await Task.Run(() => _preferences.SaveAsync(game.AppId, preferences));
                if (revision == _revision && edit == _editRevision) SaveStatus = "Saved on this PC";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { if (revision == _revision && edit == _editRevision) { _dirty = true; SaveStatus = "Could not save. Try again."; } }
        }
    }
    private void OpenExternal(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { LaunchStatus = "Could not open this location."; }
    }
    public void SetLaunchExecutable(string path)
    {
        if (!_preferencesLoaded || Game is not { IsLocalDownload: true } game) return;
        if (!string.IsNullOrEmpty(path) && !GameLaunchService.IsGameExecutable(game, path))
        { LaunchStatus = "Choose an executable inside this game's installation folder."; return; }
        LaunchExecutable = path;
    }
}
