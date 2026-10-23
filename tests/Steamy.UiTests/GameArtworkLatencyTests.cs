using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;
using Steamy.Controls;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadyGameArtworkDoesNotWaitForTheOtherImage(bool slowLogo)
    {
        var artwork = new GatedGameArtwork();
        var model = Model(new RelatedData(), new Preferences(), artwork: artwork);
        var image = TransparentLogo();
        var request = artwork.For(42);
        (slowLogo ? request.Hero : request.Logo).SetResult(image);
        model.SelectGame(Game(42));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) =>
        {
            if ((slowLogo ? model.HeroArtwork : model.LogoArtwork) is not null) ready.TrySetResult();
        };
        var activation = model.ActivateAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(activation.IsCompleted);
            if (slowLogo) { Assert.Same(image, model.HeroArtwork); Assert.Null(model.LogoArtwork); }
            else { Assert.Null(model.HeroArtwork); Assert.Equal(60, ((BitmapSource)model.LogoArtwork!).PixelWidth); }
        }
        finally
        {
            request.Hero.TrySetResult(image); request.Logo.TrySetResult(image);
            await activation.WaitAsync(TimeSpan.FromSeconds(5)); await model.DeactivateAsync();
        }
    }

    [Fact]
    public async Task FailedGameLogoDoesNotDiscardTheReadyBanner()
    {
        var artwork = new GatedGameArtwork(); var image = TransparentLogo();
        artwork.For(42).Hero.SetResult(image);
        artwork.For(42).Logo.SetException(new IOException("Logo fixture unavailable."));
        var model = Model(new RelatedData(), new Preferences(), artwork: artwork);
        model.SelectGame(Game(42));
        try { await model.ActivateAsync(); Assert.Same(image, model.HeroArtwork); Assert.Null(model.LogoArtwork); }
        finally { await model.DeactivateAsync(); }
    }

    [Fact]
    public async Task LateArtworkFromThePreviousGameCannotReplaceTheCurrentGame()
    {
        var artwork = new GatedGameArtwork(); var oldImage = TransparentLogo(); var currentImage = TransparentLogo();
        var model = Model(new RelatedData(), new Preferences(), artwork: artwork);
        model.SelectGame(Game(42)); var oldActivation = model.ActivateAsync();
        model.SelectGame(Game(43));
        artwork.For(43).Hero.SetResult(currentImage); artwork.For(43).Logo.SetResult(currentImage);
        try
        {
            await model.ActivateAsync();
            var logo = model.LogoArtwork;
            Assert.NotNull(logo); Assert.Same(currentImage, model.HeroArtwork);
            artwork.For(42).Hero.SetResult(oldImage); artwork.For(42).Logo.SetResult(oldImage);
            await oldActivation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(currentImage, model.HeroArtwork); Assert.Same(logo, model.LogoArtwork);
        }
        finally
        {
            artwork.For(42).Hero.TrySetResult(oldImage); artwork.For(42).Logo.TrySetResult(oldImage);
            await oldActivation.WaitAsync(TimeSpan.FromSeconds(5)); await model.DeactivateAsync();
        }
    }

    [Fact]
    public async Task QueuedLogoProcessingDoesNotBlockTheCallerAndCanBeCancelled()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = ArtworkRenderWorker.RunAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Artwork worker fixture was not released.");
            return true;
        }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<BitmapSource?>? trimming = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            trimming = GameLogoLayout.TrimAsync(TransparentLogo(), cancellation.Token);
            Assert.False(trimming.IsCompleted);
            cancellation.Cancel();
        }
        finally { release.Set(); await blocking.WaitAsync(TimeSpan.FromSeconds(5)); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trimming!);
    }

    [Fact]
    public async Task DownloadBannerAppearsBeforeItsLogoAndRejectsImagesAfterLeaving()
    {
        var artwork = new GatedGameArtwork(); var image = TransparentLogo();
        var store = new AppDataStore();
        store.Downloads.Add(new DownloadJob { AppId = 42, GameName = "Fixture", State = DownloadJobState.Downloading });
        var model = new DownloadsViewModel(store, new NavigationService(), Log(),
            (IDownloadManager)OfflineServiceProxy.Create(typeof(IDownloadManager)),
            (IRyuuGameDownloadService)OfflineServiceProxy.Create(typeof(IRyuuGameDownloadService)), new Settings(), artwork);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) => { if (model.HeroArtwork is not null) ready.TrySetResult(); };
        model.StartLiveStats();
        // Capture the actual load task so this windowless test can drain the
        // final request after StopLiveStats, including an intentionally late logo.
        var load = typeof(DownloadsViewModel).GetMethod("LoadHeroArtworkAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var activation = (Task)load.Invoke(model, null)!;
        artwork.For(42).Hero.SetResult(image);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(image, model.HeroArtwork); Assert.Null(model.HeroLogo);
            model.StopLiveStats();
            artwork.For(42).Logo.SetResult(image);
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(model.HeroLogo);
        }
        finally
        {
            model.StopLiveStats(); artwork.For(42).Hero.TrySetResult(image); artwork.For(42).Logo.TrySetResult(image);
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static BitmapImage TransparentLogo()
    {
        var pixels = new byte[100 * 80 * 4];
        for (var y = 50; y < 70; y++) for (var x = 20; x < 80; x++)
        { pixels[(y * 100 + x) * 4] = 200; pixels[(y * 100 + x) * 4 + 3] = 255; }
        var bitmap = BitmapSource.Create(100, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 400); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private sealed class GatedGameArtwork : IArtworkService
    {
        internal sealed class Request
        {
            internal readonly TaskCompletionSource<BitmapImage?> Hero = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<BitmapImage?> Logo = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Dictionary<int, Request> _requests = [];
        internal Request For(int appId)
        { if (!_requests.TryGetValue(appId, out var request)) _requests[appId] = request = new(); return request; }
        // Ignore cancellation deliberately: late network results must still be rejected by the model.
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => For(appId).Hero.Task;
        public Task<BitmapImage?> LoadGameLogoAsync(Game game, CancellationToken cancellationToken = default) => For(game.AppId).Logo.Task;
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
