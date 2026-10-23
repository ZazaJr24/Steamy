using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Services;
using Steamy.Pages;

namespace Steamy.UiTests;

public sealed class PaletteDispatchTests
{
    [Fact]
    public void UncachedPaletteWaitsForTheWorkerInsteadOfRenderingOnTheCaller()
    {
        OnSta(() =>
        {
            using var blocked = new BlockedWorker();
            var pending = ArtworkPalette.FromImageAsync(Artwork(), default);
            try { Assert.False(pending.IsCompleted, "Uncached palette sampling must run on the render worker."); }
            finally { blocked.Release(); }
            Assert.NotEqual(ArtworkPalette.Neutral, pending.GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void CancellingOneQueuedConsumerDoesNotCancelAnotherConsumerOfTheSameImage()
    {
        OnSta(() =>
        {
            using var blocked = new BlockedWorker();
            var source = Artwork();
            using var cancellation = new CancellationTokenSource();
            var abandoned = ArtworkPalette.FromImageAsync(source, cancellation.Token);
            var current = ArtworkPalette.FromImageAsync(source, default);
            try
            {
                cancellation.Cancel();
                Assert.True(abandoned.Wait(TimeSpan.FromSeconds(2)) || abandoned.IsCanceled);
                Assert.True(abandoned.IsCanceled);
                Assert.False(current.IsCompleted);
            }
            catch (AggregateException error) when (error.InnerException is OperationCanceledException)
            { Assert.True(abandoned.IsCanceled); Assert.False(current.IsCompleted); }
            finally { blocked.Release(); }
            Assert.Same(current.GetAwaiter().GetResult(), ArtworkPalette.FromImage(source));
        });
    }

    [Fact]
    public void CachedAndMissingArtworkDoNotWaitForTheWorker()
    {
        OnSta(() =>
        {
            var source = Artwork(); var expected = ArtworkPalette.FromImage(source);
            using var blocked = new BlockedWorker();
            var cached = ArtworkPalette.FromImageAsync(source, default);
            var missing = ArtworkPalette.FromImageAsync(null, default);
            Assert.True(cached.IsCompletedSuccessfully); Assert.Same(expected, cached.Result);
            Assert.True(missing.IsCompletedSuccessfully); Assert.Same(ArtworkPalette.Neutral, missing.Result);
        });
    }

    [Fact]
    public void AnAlreadyCancelledRequestDoesNotSampleEvenACachedImage()
    {
        OnSta(() =>
        {
            var source = Artwork(); _ = ArtworkPalette.FromImage(source);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ArtworkPalette.FromImageAsync(source, cancellation.Token).GetAwaiter().GetResult());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncSamplingPreservesRegionalAndTransparentArtworkColors(bool frozen)
    {
        OnSta(() =>
        {
            var source = Artwork(frozen);
            var reference = source.CloneCurrentValue(); reference.Freeze();
            var expected = ArtworkPalette.FromImage(reference);
            var actual = ArtworkPalette.FromImageAsync(source, default).GetAwaiter().GetResult();
            Assert.Equal(expected, actual);
            Assert.Same(actual, ArtworkPalette.FromImageAsync(source, default).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void CompletedPaletteCacheDoesNotKeepTheSourceImageAlive()
    {
        OnSta(() =>
        {
            var reference = SampleAndRelease();
            _ = ArtworkRenderWorker.RunAsync(() => 1, default).GetAwaiter().GetResult();
            for (var attempt=0; attempt<3 && reference.IsAlive; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.False(reference.IsAlive);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LibraryUsesOnlyItsTwoEdgePalettesAndPreservesTheResultingPixels(bool light)
    {
        OnSta(() =>
        {
            var first=Artwork(); var last=Artwork();
            var left=ArtworkPalette.FromImage(first); var right=ArtworkPalette.FromImage(last);
            var combined=new ArtworkPalette(left.Left,right.Right);
            Brush expected=light ? PaperAmbient.Create(combined) : Brushes.Black;
            using var blocked=new BlockedWorker();
            var actual=LibraryPage.CreateLibraryAmbientAsync([null,first,Artwork(),Artwork(),Artwork(),last],light,default);
            Assert.True(actual.IsCompletedSuccessfully,"Unused middle artwork must not queue palette work.");
            Assert.True(actual.Result.IsFrozen);
            Assert.Equal(Render(expected,light),Render(actual.Result,light));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LibraryWithoutArtworkKeepsItsThemeFallbackWithoutStartingWorkerWork(bool light)
    {
        OnSta(() =>
        {
            using var blocked=new BlockedWorker();
            var result=LibraryPage.CreateLibraryAmbientAsync([null,null],light,default);
            Assert.True(result.IsCompletedSuccessfully);
            Assert.True(result.Result.IsFrozen);
            var expected=light ? (Brush)PaperAmbient.Create(ArtworkPalette.Neutral) : Brushes.Black;
            Assert.Equal(Render(expected,light),Render(result.Result,light));
        });
    }

    [Fact]
    public void DarkLibraryIsExactlyBlackWithoutQueueingArtworkAnalysis()
    {
        OnSta(() =>
        {
            using var blocked = new BlockedWorker();
            var result = LibraryPage.CreateLibraryAmbientAsync([Artwork(), Artwork()], false, default);
            Assert.True(result.IsCompletedSuccessfully);
            var pixels = Render(result.Result, false);
            Assert.All(pixels.Where((_, index) => index % 4 != 3), channel => Assert.Equal(0, channel));
            Assert.All(pixels.Where((_, index) => index % 4 == 3), channel => Assert.Equal(255, channel));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallationSurfaceUsesTheSelectedThemeAndKeepsItsFrozenTint(bool light)
    {
        OnSta(() =>
        {
            var brush = InstallationAmbient.Create(new(Colors.Red, Colors.Blue), light);
            Assert.True(brush.IsFrozen);
            var pixels=Render(brush,light);
            var channels = pixels.Where((_, index) => index%4 != 3).ToArray();
            if (light) Assert.InRange(channels.Min(),230,255);
            else Assert.InRange(channels.Max(),20,65);
        });
    }

    private static byte[] Render(Brush brush,bool light)
    {
        var visual = new DrawingVisual();
        using (var dc=visual.RenderOpen())
        { dc.DrawRectangle(light ? Brushes.White : Brushes.Black,null,new Rect(0,0,64,64)); dc.DrawRectangle(brush,null,new Rect(0,0,64,64)); }
        var bitmap = new RenderTargetBitmap(64,64,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels=new byte[64*64*4]; bitmap.CopyPixels(pixels,64*4,0); return pixels;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SampleAndRelease()
    {
        var source = Artwork();
        _ = ArtworkPalette.FromImageAsync(source, default).GetAwaiter().GetResult();
        return new WeakReference(source);
    }
    private static DrawingImage Artwork(bool frozen = true)
    {
        var brush = new LinearGradientBrush();
        brush.GradientStops.Add(new(Color.FromArgb(60,180,40,20),0));
        brush.GradientStops.Add(new(Color.FromRgb(230,80,25),.25));
        brush.GradientStops.Add(new(Color.FromRgb(20,80,230),1));
        var image = new DrawingImage(new GeometryDrawing(brush,null,new RectangleGeometry(new Rect(0,0,192,108))));
        if (frozen) image.Freeze(); return image;
    }
    private sealed class BlockedWorker : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private readonly Task<int> _work;
        internal BlockedWorker()
        {
            _work = ArtworkRenderWorker.RunAsync(() => { _entered.Set(); if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return 0; },default);
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)));
        }
        internal void Release() => _release.Set();
        public void Dispose() { Release(); _work.GetAwaiter().GetResult(); _entered.Dispose(); _release.Dispose(); }
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure=error; } }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
