using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ShellBackdropLifecycleTests
{
    [Fact]
    public void HomeRenderingNeverBuildsAmbientOnTheWindowThreadAndCancelsOnUnload() => OnSta(() =>
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var worker = ArtworkRenderWorker.RunAsync(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Home artwork test worker was not released.");
            return true;
        }, CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var first = Picture(35); var latest = Picture(190);
        var view = new DashboardBackdropView { Artwork = first, HeroHeight = 120 };
        var host = new System.Windows.Controls.Grid(); host.Children.Add(view);
        var window = new Window { Width = 360, Height = 300, Content = host, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            view.Artwork = latest;
            var frame = new RenderTargetBitmap(360, 300, 96, 96, PixelFormats.Pbgra32);
            frame.Render(view);
            var inputHandled = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputHandled = true));
            PumpUntil(() => inputHandled);
            Assert.False(worker.IsCompleted);
            Assert.Null(view.AmbientBitmap);
            release.Set();
            PumpUntil(() => view.AmbientBitmap is not null);
            Assert.Same(ArtworkContinuation.For(latest, view.ActualWidth / view.HeroHeight), view.AmbientBitmap);
            var cached = view.AmbientBitmap;
            view.Artwork = null; view.Artwork = latest;
            Assert.Same(cached, view.AmbientBitmap);
            view.Artwork = Picture(90);
            host.Children.Remove(view); Pump();
            var drained = ArtworkRenderWorker.RunAsync(() => true, CancellationToken.None);
            PumpUntil(() => drained.IsCompleted); Pump();
            Assert.Null(view.Artwork); Assert.Null(view.AmbientBitmap);
        }
        finally { release.Set(); window.Close(); Pump(); PumpUntil(() => worker.IsCompleted); }
    });

    [Fact]
    public void RenderingKeepsInputResponsiveDiscardsReplacedArtworkAndCannotResurrectClearedArtwork() => OnSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = ArtworkRenderWorker.RunAsync(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Artwork test worker was not released.");
            return true;
        }, CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var service = new ShellBackdropService();
        var first = Picture(30); var latest = Picture(180);
        var applied = new List<ImageSource?>();
        service.Changed += (_, _) => { if (service.BlurredArtwork is not null) applied.Add(service.Artwork); };
        try
        {
            service.SetArtwork(first); service.SetArtwork(latest);
            var inputHandled = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputHandled = true));
            PumpUntil(() => inputHandled);
            Assert.False(worker.IsCompleted);
            Assert.Null(service.BlurredArtwork);
            release.Set();
            PumpUntil(() => service.BlurredArtwork is not null);
            Assert.Same(latest, service.Artwork);
            Assert.Single(applied); Assert.Same(latest, applied[0]);
            var cached = Assert.IsAssignableFrom<BitmapSource>(service.BlurredArtwork);
            Assert.True(cached.IsFrozen);
            service.Clear(); service.SetArtwork(latest);
            Assert.Same(cached, service.BlurredArtwork);

            service.SetArtwork(Picture(90)); service.Clear();
            var drained = ArtworkRenderWorker.RunAsync(() => true, CancellationToken.None);
            PumpUntil(() => drained.IsCompleted);
            Pump();
            Assert.Null(service.Artwork); Assert.Null(service.BlurredArtwork);
        }
        finally { release.Set(); service.Clear(); PumpUntil(() => worker.IsCompleted); }
    });

    private static void PumpUntil(Func<bool> complete)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && deadline.Elapsed < TimeSpan.FromSeconds(10)) Pump();
        Assert.True(complete(), "Artwork rendering did not settle.");
    }

    [Theory]
    [InlineData(Visibility.Collapsed)]
    [InlineData(Visibility.Hidden)]
    public void HiddenMinimizedAndRemovedGlassReleasesArtworkAndResumesWithCurrentSource(Visibility hidden) => OnSta(() =>
    {
        var original = App.Services;
        var motion = MotionPreferences.ReduceEffects;
        var backdrop = new Backdrop();
        using var services = new ServiceCollection().AddSingleton<IShellBackdropService>(backdrop).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        MotionPreferences.Configure(false);
        var view = new ShellBackdropView { BaseBrush = Brushes.Black, IsGlass = true, FrostRadius = 4 };
        var host = new System.Windows.Controls.Grid(); host.Children.Add(view);
        var window = new Window { Width = 320, Height = 220, Content = host, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            Assert.Equal(1, backdrop.Subscribers);
            var old = PaintSequence(backdrop, view);
            view.Visibility = hidden;
            Assert.Equal(0, backdrop.Subscribers);
            Assert.False(view.HasAnimatedProperties); Assert.Null(view.Effect);
            Assert.False(HasArtwork(view));
            backdrop.Clear(); Collect(old);

            var latest = Picture(90);
            backdrop.SetArtwork(latest);
            Assert.Equal(0, backdrop.Subscribers);
            view.Visibility = Visibility.Visible; Pump();
            Assert.Equal(1, backdrop.Subscribers);
            Assert.True(HasArtwork(view, latest));

            window.WindowState = WindowState.Minimized; Pump();
            Assert.Equal(0, backdrop.Subscribers);
            Assert.False(view.HasAnimatedProperties); Assert.Null(view.Effect);
            Assert.False(HasArtwork(view));
            window.WindowState = WindowState.Normal; Pump();
            Assert.Equal(1, backdrop.Subscribers);
            Assert.True(HasArtwork(view, latest));

            host.Children.Remove(view); Pump();
            Assert.Equal(0, backdrop.Subscribers);
            Assert.False(HasArtwork(view));
        }
        finally
        {
            window.Close(); Pump();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, original);
            MotionPreferences.Configure(motion);
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PaintSequence(Backdrop backdrop, ShellBackdropView view)
    {
        var first = Picture(30); var second = Picture(180);
        backdrop.SetArtwork(first); Pump();
        Assert.True(HasArtwork(view, first));
        backdrop.SetArtwork(second); Pump();
        Assert.True(HasArtwork(view, second));
        return [new(first), new(second)];
    }
    private static BitmapSource Picture(byte channel)
    {
        var pixels = Enumerable.Repeat(channel, 64 * 32 * 4).ToArray();
        var bitmap = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        bitmap.Freeze(); return bitmap;
    }
    private static bool HasArtwork(Visual visual, ImageSource? expected = null)
    {
        if (HasArtwork(VisualTreeHelper.GetDrawing(visual), expected)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child && HasArtwork(child, expected)) return true;
        return false;
    }
    private static bool HasArtwork(Drawing? drawing, ImageSource? expected = null)
    {
        if (drawing is DrawingGroup group) return group.Children.Any(child => HasArtwork(child, expected));
        var image = drawing switch { ImageDrawing item => item.ImageSource, GeometryDrawing { Brush: ImageBrush brush } => brush.ImageSource, _ => null };
        return image is not null && (expected is null || ReferenceEquals(image, expected));
    }
    private static void Collect(WeakReference[] images)
    {
        for (var i = 0; i < 5 && images.Any(reference => reference.IsAlive); i++)
        { Pump(); GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        Assert.DoesNotContain(images, reference => reference.IsAlive);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Backdrop lifecycle timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Backdrop : IShellBackdropService
    {
        private EventHandler? _changed;
        public int Subscribers { get; private set; }
        public ImageSource? Artwork { get; private set; }
        public ImageSource? BlurredArtwork => Artwork;
        public double HeroHeight => 160;
        public double HeroTop => 0;
        public double ScrollOffset => 0;
        public event EventHandler? Changed
        { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
        public void SetArtwork(ImageSource? image) { Artwork = image; _changed?.Invoke(this, EventArgs.Empty); }
        public void Clear() => SetArtwork(null);
        public void SetViewport(double height, double top, double offset) { }
    }
}
