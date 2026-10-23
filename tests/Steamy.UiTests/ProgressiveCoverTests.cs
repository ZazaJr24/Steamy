using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ProgressiveCoverTests
{
    [Theory]
    [InlineData("game", false)]
    [InlineData("game", true)]
    [InlineData("spotlight", false)]
    [InlineData("spotlight", true)]
    [InlineData("catalog", false)]
    [InlineData("catalog", true)]
    public async Task ReadyArtworkAppearsWhileItsCompanionRequestIsStillBlocked(string route, bool blockPortrait)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-progressive-cover-" + Guid.NewGuid().ToString("N"));
        using var handler = new PausedImageHandler(blockPortrait);
        using var http = new HttpClient(handler);
        using var artwork = new SteamArtworkService(http, folder);
        using var catalog = new SteamCatalogService(http, Path.Combine(folder, "catalog"));
        var game = new Game { AppId = 480 };
        var item = new SteamCatalogItem { AppId = 480, PortraitImageUrl = "https://shared.akamai.steamstatic.com/portrait.png", HeaderImageUrl = "https://shared.akamai.steamstatic.com/header.png" };
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var property = blockPortrait ? nameof(Game.HeaderImage) : nameof(Game.ArtworkImage);
        ((INotifyPropertyChanged)(route == "catalog" ? item : game)).PropertyChanged += (_, args) => { if (args.PropertyName == property) published.TrySetResult(); };
        Task? operation = null;
        try
        {
            operation = route switch
            {
                "catalog" => catalog.EnsureArtworkAsync(item, includeHeader: true),
                "spotlight" => artwork.LoadSpotlightCoverAsync(new SpotlightGame { AppId = 480, PortraitUrl = item.PortraitImageUrl, HeaderUrl = item.HeaderImageUrl }, game, ImagePriority.Visible),
                _ => artwork.LoadAsync(game)
            };
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            Assert.NotNull(route == "catalog" ? blockPortrait ? item.HeaderImage : item.ArtworkImage : blockPortrait ? game.HeaderImage : game.ArtworkImage);
        }
        finally
        {
            handler.Release.TrySetResult();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task CancelledPublicationCannotReplaceAnAlreadyVisibleImage()
    {
        var request = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var published = false;
        var operation = SteamArtworkService.PublishImageAsync(request.Task, _ => published = true, cancellation.Token);
        cancellation.Cancel(); request.SetResult(new BitmapImage());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(published);
    }

    private sealed class PausedImageHandler(bool blockPortrait) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _png = CreateImage();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var header = request.RequestUri!.AbsolutePath.Contains("header", StringComparison.Ordinal);
            if (header != blockPortrait) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_png) };
        }
        private static byte[] CreateImage()
        {
            var image = BitmapSource.Create(4, 6, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 4 * 6 * 4).ToArray(), 16);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
    }
}
