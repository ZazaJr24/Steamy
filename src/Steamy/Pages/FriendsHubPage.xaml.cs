using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;

namespace Steamy.Pages;

public partial class FriendsHubPage : Page
{
    private readonly IAppDataStore _store;
    private readonly IArtworkService _artwork;
    private readonly ISteamCatalogService? _catalog;
    private CancellationTokenSource? _load;
    private readonly IDiscordSocialService _discord;
    public DiscordFriendsViewModel Social { get; }
    public ObservableCollection<DiscordFriend> People { get; } = [];
    public ObservableCollection<HubGameCard> Games { get; } = [];
    public FriendsHubPage()
    {
        InitializeComponent();
        _store = App.Services.GetRequiredService<IAppDataStore>(); _artwork = App.Services.GetRequiredService<IArtworkService>();
        _catalog = App.Services.GetService<ISteamCatalogService>();
        _discord = App.Services.GetRequiredService<IDiscordSocialService>();
        Social = App.Services.GetRequiredService<DiscordFriendsViewModel>(); DataContext = this;
        Loaded += (_, _) => { _store.Games.CollectionChanged += GamesChanged; _discord.Changed += PeopleChanged; RefreshPeople(); Refresh(); };
        Unloaded += (_, _) => { _store.Games.CollectionChanged -= GamesChanged; _discord.Changed -= PeopleChanged; _load?.Cancel(); _load?.Dispose(); _load = null; };
        SizeChanged += (_, _) => { HubContent.Margin = new Thickness(ActualWidth < 760 ? 24 : 32, 26, ActualWidth < 760 ? 24 : 32, 36); Resources["HubPeopleColumns"] = ActualWidth < 760 ? 1 : 2; };
    }
    private void PeopleChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) RefreshPeople(); })); return; }
        if (IsLoaded) RefreshPeople();
    }
    private void RefreshPeople()
    {
        var next = _discord.Snapshot.Relationships.Where(friend => friend.IsFriend).OrderByDescending(friend => friend.IsOnline)
            .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(8).ToArray();
        for (var i = People.Count - 1; i >= 0; i--) if (!next.Any(friend => friend.Id == People[i].Id)) People.RemoveAt(i);
        for (var i = 0; i < next.Length; i++)
        {
            var existing = People.Select((friend, index) => (friend, index)).FirstOrDefault(item => item.friend.Id == next[i].Id);
            if (existing.friend is null) People.Insert(i, next[i]);
            else { if (existing.index != i) People.Move(existing.index, i); if (People[i] != next[i]) People[i] = next[i]; }
        }
        PeopleEmpty.Visibility = People.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void GamesChanged(object? sender, NotifyCollectionChangedEventArgs args) => Refresh();
    private async void Refresh()
    {
        _load?.Cancel(); _load?.Dispose(); _load = new(); var token = _load.Token;
        var games = _store.Games.Where(game => game.InstallState == GameInstallState.Installed && game.AppId > 0).Take(4).ToArray();
        Games.Clear(); foreach (var game in games) Games.Add(new(game));
        EmptyLibrary.Visibility = games.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!TogetherDisclosure.IsExpanded) return;
        try
        {
            await _artwork.LoadHeadersAsync(games, token);
            if (token.IsCancellationRequested) return;
            var cards = Games.ToArray(); foreach (var card in cards) card.Image = card.Game.HeaderImage;
            await Task.WhenAll(cards.Select(card => LoadPreviewAsync(card, token)));
        }
        catch (OperationCanceledException) { } catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException) { }
    }
    private void Games_Expanded(object sender, RoutedEventArgs args) { if (IsLoaded) Refresh(); }
    private async Task LoadPreviewAsync(HubGameCard card, CancellationToken token)
    {
        if (_catalog is null) return;
        try
        {
            var details = await _catalog.GetDetailsAsync(card.Game.AppId, token);
            if (details?.Screenshots?.FirstOrDefault()?.ThumbnailUrl is not { Length: > 0 } url) return;
            var image = await _artwork.LoadSpotlightHeaderAsync(new SpotlightGame { AppId = card.Game.AppId, HeaderUrl = url }, token);
            if (!token.IsCancellationRequested && image is not null) card.Image = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException) { }
    }
    private MainWindow? Shell => Window.GetWindow(this) as MainWindow;
    private void Friends_Click(object sender, RoutedEventArgs args) { Shell?.OpenSocialDock(); Shell?.SocialDockPage?.RevealFriends(); }
    private void Messages_Click(object sender, RoutedEventArgs args) { Shell?.OpenSocialDock(); if (Social.SelectedFriend?.IsFriend == true) Shell?.SocialDockPage?.RevealConversation(); else Shell?.SocialDockPage?.RevealFriends(); }
    private void Person_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: DiscordFriend friend }) return;
        Social.SearchText = ""; Social.SelectedFilter = "Friends"; Social.SelectedFriend = friend;
        Shell?.OpenSocialDock(); Shell?.SocialDockPage?.RevealConversation();
    }
    private void Party_Click(object sender, RoutedEventArgs args) { Shell?.OpenSocialDock(); Shell?.SocialDockPage?.RevealParty(); }
    private void Browse_Click(object sender, RoutedEventArgs args) => App.Services.GetRequiredService<INavigationService>().Navigate<LibraryPage>();
    private void Game_Click(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement { Tag: HubGameCard card }) { App.Services.GetRequiredService<DiscordFriendsViewModel>().SelectedGame = card.Game; Party_Click(sender, args); } }
}
public sealed class HubGameCard(Game game) : ObservableObject
{
    private BitmapImage? _image;
    public Game Game { get; } = game;
    public string Name => Game.Name;
    public string CoverColor => Game.CoverColor;
    public BitmapImage? Image { get => _image; set => SetProperty(ref _image, value); }
}
