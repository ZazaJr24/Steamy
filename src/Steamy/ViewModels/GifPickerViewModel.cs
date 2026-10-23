using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class GifPickerViewModel : ObservableObject, IDisposable
{
    private readonly IGifSearchService _service;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _request;
    private string _query = "", _status = "", _apiKey, _link = "";
    private bool _busy, _favorites, _open, _more;
    private int _page, _generation;
    public ObservableCollection<GifItem> Items { get; } = [];
    public string[] Providers { get; } = ["KLIPY", "GIPHY"];
    public string Provider { get => _settings.GifProvider; set { if (value == Provider || value is not ("KLIPY" or "GIPHY")) return; _settings.GifProvider = value; _settings.GifApiKey = ""; ApiKey = ""; _ = SaveAndReloadAsync(); OnPropertyChanged(); OnPropertyChanged(nameof(SearchPlaceholder)); OnPropertyChanged(nameof(Attribution)); } }
    public string SearchPlaceholder => "Search " + _service.Provider;
    public string Attribution => "Powered by " + _service.Provider;
    public string Query { get => _query; set { if (SetProperty(ref _query, value) && _open) _ = LoadAsync(false, true); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string Link { get => _link; set { if (SetProperty(ref _link, value)) UseLinkCommand.NotifyCanExecuteChanged(); } }
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) MoreCommand.NotifyCanExecuteChanged(); } }
    public bool ShowFavorites { get => _favorites; set { if (SetProperty(ref _favorites, value) && _open) _ = LoadAsync(false); } }
    public IAsyncRelayCommand SaveKeyCommand { get; }
    public IAsyncRelayCommand MoreCommand { get; }
    public IRelayCommand<GifItem> PickCommand { get; }
    public IAsyncRelayCommand<GifItem> FavoriteCommand { get; }
    public IRelayCommand UseLinkCommand { get; }
    public IRelayCommand OpenProviderCommand { get; }
    public event EventHandler<string>? Picked;
    public GifPickerViewModel(IGifSearchService service, ISettingsService settings)
    {
        _service = service; _settingsService = settings; _settings = settings.Load(); _apiKey = _settings.GifApiKey;
        MoreCommand = new AsyncRelayCommand(() => LoadAsync(true), () => _more && !Busy && !ShowFavorites);
        SaveKeyCommand = new AsyncRelayCommand(async () => { _settings.GifApiKey = ApiKey.Trim(); await SaveAndReloadAsync(); });
        PickCommand = new RelayCommand<GifItem>(item => { if (item is not null) Picked?.Invoke(this, item.ShareUrl); });
        UseLinkCommand = new RelayCommand(() => Picked?.Invoke(this, ChatMediaPolicy.ImageUrl(Link)!), () => ChatMediaPolicy.ImageUrl(Link.Trim()) == Link.Trim());
        FavoriteCommand = new AsyncRelayCommand<GifItem>(async item =>
        {
            if (item is null) return;
            var favorite = item.Provider + ":" + item.Id;
            var favorites = _settings.GifFavoriteIds.ToList();
            if (!favorites.Remove(favorite)) { if (favorites.Count >= 50) favorites.RemoveAt(0); favorites.Add(favorite); }
            _settings.GifFavoriteIds = favorites.ToArray();
            try { await _settingsService.SaveAsync(_settings); Status = favorites.Contains(favorite) ? "Added to favorites." : "Removed from favorites."; }
            catch (Exception) { Status = "Couldn't save GIF favorites."; }
            if (ShowFavorites) await LoadAsync(false);
        });
        OpenProviderCommand = new RelayCommand(() =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_service.Provider == "KLIPY" ? "https://klipy.com/" : "https://giphy.com/") { UseShellExecute = true }); }
            catch (Exception) { Status = "Couldn't open the GIF website."; }
        });
    }
    public Task OpenAsync() { _open = true; return LoadAsync(false); }
    public void Suspend() { _open = false; _generation++; _request?.Cancel(); Items.Clear(); Busy = false; }
    private async Task SaveAndReloadAsync()
    {
        try { await _settingsService.SaveAsync(_settings); if (_open) await LoadAsync(false); }
        catch (Exception) { Status = "Couldn't save the GIF settings."; }
    }
    private async Task LoadAsync(bool append, bool debounce = false)
    {
        _request?.Cancel(); _request?.Dispose(); var request = _request = new CancellationTokenSource();
        var generation = ++_generation; var page = append ? _page + 1 : 1;
        if (!append) Items.Clear(); Busy = true; _more = false;
        try
        {
            if (!_service.IsConfigured) { Status = $"Paste a direct GIF link, or add a {_service.Provider} app key to search here."; return; }
            if (debounce) await Task.Delay(300, request.Token);
            Status = "Loading GIFs…";
            var prefix = _service.Provider + ":";
            var result = ShowFavorites ? await _service.FavoritesAsync(_settings.GifFavoriteIds.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => id[prefix.Length..]).ToArray(), request.Token)
                : await _service.SearchAsync(Query, page, request.Token);
            if (generation != _generation || !_open || request.IsCancellationRequested) return;
            foreach (var item in result.Items) Items.Add(item);
            _page = page; _more = result.HasMore;
            Status = Items.Count == 0 ? ShowFavorites ? "No favorites yet. Use the star next to a GIF." : "No GIFs found. Try another search." : "Select a GIF to add it to your message.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (generation == _generation) Status = error is GifProviderException ? error.Message : "GIF search is unavailable. You can still paste a GIF link."; }
        finally { if (generation == _generation) { Busy = false; MoreCommand.NotifyCanExecuteChanged(); } }
    }
    public void Dispose() { Suspend(); _request?.Dispose(); }
}
