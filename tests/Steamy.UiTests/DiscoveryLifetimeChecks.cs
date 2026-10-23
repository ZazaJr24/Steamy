using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDiscoveryLifetimeOffscreen(IServiceProvider services)
    {
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        var artwork = new GatedDiscoveryArtwork();
        var feed = new DiscoveryFeedFixture();
        var model = ActivatorUtilities.CreateInstance<DashboardViewModel>(services, artwork, feed);
        var image = DiscoveryImage();
        try
        {
            var first = model.FeaturedGame!;
            var loading = model.EnsureDiscoveryArtworkAsync();
            PumpUntil(() => artwork.Calls.Any(call => call.Kind == "hero") && artwork.Calls.Any(call => call.Kind == "logo"));
            var abandoned = artwork.Calls.ToArray();
            model.StopLiveStats();
            Assert.All(abandoned, call => Assert.True(call.Token.IsCancellationRequested));
            PumpUntil(() => loading.IsCompleted);
            Assert.Null(first.HeroArtwork); Assert.Null(first.LogoArtwork);
            foreach (var call in abandoned) // Provider deliberately ignores cancellation, then returns or faults late.
                if (call.Kind == "logo") call.Result.TrySetException(new IOException("Late offline logo failure"));
                else call.Result.TrySetResult(image);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.Null(first.HeroArtwork); Assert.Null(first.LogoArtwork);
            Assert.DoesNotContain(artwork.Calls, call => call.AppId == 42002);
            var stoppedCalls = artwork.Calls.Count;
            var suppressed = model.EnsureDiscoveryArtworkAsync(force: true);
            Assert.True(suppressed.IsCompleted);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert.Equal(stoppedCalls, artwork.Calls.Count);

            model.StartLiveStats();
            PumpUntil(() => artwork.Calls.Count(call => call.AppId == 42001 && call.Kind == "hero") == 2);
            foreach (var call in artwork.Calls.Except(abandoned).Where(call => call.AppId == 42001)) call.Result.TrySetResult(image);
            PumpUntil(() => ReferenceEquals(first.HeroArtwork, image) && ReferenceEquals(first.LogoArtwork, image));
            PumpUntil(() => artwork.Calls.Any(call => call.AppId == 42002 && call.Kind == "header"));
            var next = model.DiscoverGames.Single(feature => feature.Game.AppId == 42002);
            var prefetch = artwork.Calls.Where(call => call.AppId == 42002).ToArray();
            prefetch.Single(call => call.Kind == "header").Result.TrySetResult(image);
            PumpUntil(() => ReferenceEquals(next.Game.HeaderImage, image));
            Assert.Null(next.HeroArtwork); // A ready thumbnail must not wait for the large banner.
            model.StopLiveStats();
            Assert.All(prefetch, call => Assert.True(call.Token.IsCancellationRequested));
            foreach (var call in prefetch) call.Result.TrySetResult(image);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.Null(next.HeroArtwork);
            model.StartLiveStats(); // Cached current hero must still restart the cancelled next-image prefetch.
            PumpUntil(() => artwork.Calls.Count(call => call.AppId == 42002 && call.Kind == "hero") == 2);
            PumpUntil(() => artwork.Calls.Count(call => call.AppId == 42002 && call.Kind == "logo") == 2);
            foreach (var call in artwork.Calls.Except(prefetch).Where(call => call.AppId == 42002))
            { Assert.False(call.Token.IsCancellationRequested); call.Result.TrySetResult(image); }
            PumpUntil(() => ReferenceEquals(next.HeroArtwork, image) && ReferenceEquals(next.LogoArtwork, image));

            var blockedFeed = new DiscoveryFeedFixture { BlockRefresh = true };
            var otherArtwork = new GatedDiscoveryArtwork();
            var other = ActivatorUtilities.CreateInstance<DashboardViewModel>(services, otherArtwork, blockedFeed);
            try
            {
                var refresh = other.EnsureDiscoveryArtworkAsync();
                PumpUntil(() => blockedFeed.Requested);
                other.StopLiveStats();
                Assert.True(blockedFeed.Token.IsCancellationRequested);
                PumpUntil(() => refresh.IsCompleted);
                blockedFeed.Pending.SetResult(new SpotlightSnapshot(DateTimeOffset.Now, Array.Empty<SpotlightGame>()));
                otherArtwork.ReleaseAll(image); PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.Equal(2, other.DiscoverGames.Count);
            }
            finally { other.StopLiveStats(); otherArtwork.ReleaseAll(image); }
        }
        finally { model.StopLiveStats(); artwork.ReleaseAll(image); }
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    }

    private static BitmapImage DiscoveryImage()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes);
        return ImagePipeline.Decode(bytes.ToArray(), 2)!;
    }

    private sealed class DiscoveryFeedFixture : ISpotlightService
    {
        public bool BlockRefresh, Requested;
        public CancellationToken Token;
        public TaskCompletionSource<SpotlightSnapshot> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SpotlightSnapshot Cached { get; } = new(DateTimeOffset.Now, new[] { 42001, 42002 }.Select(id => new SpotlightGame
            { AppId = id, Name = "Offline discovery " + id, ComingSoon = true, ReleaseDate = DateOnly.FromDateTime(DateTime.Now).AddDays(10) }).ToArray());
        public Task<SpotlightSnapshot> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        { Requested = true; Token = cancellationToken; return BlockRefresh ? Pending.Task : Task.FromResult(Cached); }
    }

    private sealed class GatedDiscoveryArtwork : IArtworkService
    {
        internal sealed record Call(int AppId, string Kind, CancellationToken Token)
        { internal TaskCompletionSource<BitmapImage?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal System.Collections.Concurrent.ConcurrentBag<Call> Calls = new();
        private BitmapImage? _release;
        private Task<BitmapImage?> Request(int id, string kind, CancellationToken token)
        {
            var call = new Call(id, kind, token); Calls.Add(call);
            if (_release is { } image) call.Result.TrySetResult(image);
            return call.Result.Task;
        }
        internal void ReleaseAll(BitmapImage image) { _release = image; foreach (var call in Calls) call.Result.TrySetResult(image); }
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Request(appId, "hero", cancellationToken);
        public Task<BitmapImage?> LoadSpotlightLogoAsync(SpotlightGame game, CancellationToken cancellationToken = default) => Request(game.AppId, "logo", cancellationToken);
        public Task<BitmapImage?> LoadSpotlightHeaderAsync(SpotlightGame game, CancellationToken cancellationToken = default) => Request(game.AppId, "header", cancellationToken);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
