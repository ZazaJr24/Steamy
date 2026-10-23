using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Controls;
using Steamy.Services;
using Xunit.Abstractions;

namespace Steamy.UiTests;

public sealed class BackdropFrameWorkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollingReusesSmallPaintMaterialsInsteadOfRebuildingThemEveryFrame(bool light)
    {
        OnSta(() => WithTheme(light, () =>
        {
            var view = View(); var draw = Frame(view); var visual = new DrawingVisual();
            void Paint(int index)
            {
                using var context = visual.RenderOpen();
                draw(context, true, new Rect(0, 180 + index % 120, 980, 620));
            }
            for (var index = 0; index < 20; index++) Paint(index);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 240; index++) Paint(index);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"{(light ? "Light" : "Dark")}: 240 viewport recordings allocated {allocated:N0} bytes ({allocated / 240d:N0}/frame). No Application or Window.");
            Assert.InRange(allocated / 240d, 0, 3000);
            Assert.InRange(VisualTreeHelper.GetDrawing(visual).Bounds.Height, 619, 621);
            GC.KeepAlive(view);
        }));
    }

    [Fact]
    public void ClosingPreviewDropsItsCachedPaintSources()
    {
        OnSta(() =>
        {
            var view = View(); var draw = Frame(view); var visual = new DrawingVisual();
            var sources = PaintPreview(view, draw, visual);
            typeof(DashboardBackdropView).GetMethod("SetPreviewArtwork", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [null, null]);
            using (var context = visual.RenderOpen()) draw(context, true, new Rect(0, 700, 980, 620));
            for (var attempt = 0; attempt < 5 && sources.Any(source => source.IsAlive); attempt++)
            { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            Assert.All(sources, source => Assert.False(source.IsAlive));
            GC.KeepAlive(view); GC.KeepAlive(visual);
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void ResizingAndThemeChangesMatchFreshPaintAtDifferentBitmapDpi(int dpi)
    {
        OnSta(() => WithTheme(false, () =>
        {
            var view = View(); var draw = Frame(view); var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) draw(context, true, null);
            foreach (var light in new[] { true, false })
            {
                typeof(UiThemeService).GetField("_isLight", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, light);
                foreach (var width in new[] { 980d, 1440d, 1920d })
                {
                    var size = new Size(width, 2800);
                    view.HeroHeight = width / 3; view.Measure(size); view.Arrange(new Rect(size));
                    using (var context = visual.RenderOpen()) draw(context, true, null);
                    var fresh = new DashboardBackdropView { HeroHeight = view.HeroHeight, Artwork = view.Artwork };
                    fresh.Measure(size); fresh.Arrange(new Rect(size));
                    var expected = new DrawingVisual();
                    using (var context = expected.RenderOpen()) Frame(fresh)(context, true, null);
                    Assert.Equal(Pixels(expected, dpi), Pixels(visual, dpi));
                }
            }
        }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PaintPreview(DashboardBackdropView view, Action<DrawingContext, bool, Rect?> draw, DrawingVisual visual)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var current = Picture(51); var previous = Picture(90);
        typeof(DashboardBackdropView).GetField("_previewArtwork", flags)!.SetValue(view, current);
        typeof(DashboardBackdropView).GetField("_previewBrush", flags)!.SetValue(view, new ImageBrush(current) { Stretch = Stretch.Fill });
        typeof(DashboardBackdropView).GetField("_previousPreviewBrush", flags)!.SetValue(view, new ImageBrush(previous) { Stretch = Stretch.Fill });
        foreach (var name in new[] { "PreviewBlendProperty", "PreviewAmountProperty" })
            view.SetValue((DependencyProperty)typeof(DashboardBackdropView).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, .8d);
        using var context = visual.RenderOpen(); draw(context, true, new Rect(0, 700, 980, 620));
        return [new(current), new(previous)];
    }

    private static byte[] Pixels(DrawingVisual visual, int dpi)
    {
        var image = new RenderTargetBitmap(360, 300, dpi, dpi, PixelFormats.Pbgra32); image.Render(visual);
        var pixels = new byte[360 * 300 * 4]; image.CopyPixels(pixels, 360 * 4, 0); return pixels;
    }

    [Fact]
    public void AnUnloadedCachedBackdropReleasesItsRecordedArtwork()
    {
        OnSta(() =>
        {
            var view = View();
            var reference = PaintOwnFrame(view);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var pump = new System.Windows.Threading.DispatcherFrame();
            view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => pump.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(pump);
            for (var attempt = 0; attempt < 5 && reference.IsAlive; attempt++)
            { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            Assert.Null(view.Artwork);
            Assert.False(reference.IsAlive, "An unloaded cached view must release its WPF drawing commands as well as its source property.");
            GC.KeepAlive(view);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PaintOwnFrame(DashboardBackdropView view)
    {
        var source = Picture(101); view.Artwork = source;
        var frame = new RenderTargetBitmap(980, 620, 96, 96, PixelFormats.Pbgra32); frame.Render(view);
        Assert.NotNull(VisualTreeHelper.GetDrawing(view));
        return new(source);
    }

    [Fact]
    public void ReplacingAndClearingArtworkDoesNotKeepOldPaintSourcesAlive()
    {
        OnSta(() =>
        {
            var view = View(); var draw = Frame(view); var visual = new DrawingVisual();
            var first = PaintFirst(view, draw, visual);
            ReplaceAndPaint(view, draw, visual);
            for (var attempt = 0; attempt < 4 && first.IsAlive; attempt++)
            { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            Assert.False(first.IsAlive, "Cached paint materials must not retain replaced artwork.");
            var second = ClearArtwork(view, draw, visual);
            for (var attempt = 0; attempt < 4 && second.IsAlive; attempt++)
            { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            Assert.False(second.IsAlive, "Clearing a cached view must also clear its image brushes.");
            GC.KeepAlive(view); GC.KeepAlive(visual);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplaceAndPaint(DashboardBackdropView view, Action<DrawingContext, bool, Rect?> draw, DrawingVisual visual)
    {
        view.Artwork = Picture(130);
        using var context = visual.RenderOpen(); draw(context, true, new Rect(0, 0, 980, 620));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PaintFirst(DashboardBackdropView view, Action<DrawingContext, bool, Rect?> draw, DrawingVisual visual)
    {
        var image = Picture(70); view.Artwork = image;
        using var context = visual.RenderOpen(); draw(context, true, new Rect(0, 0, 980, 620));
        return new(image);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClearArtwork(DashboardBackdropView view, Action<DrawingContext, bool, Rect?> draw, DrawingVisual visual)
    {
        var reference = new WeakReference(view.Artwork!); view.Artwork = null;
        using var context = visual.RenderOpen(); draw(context, true, new Rect(0, 0, 980, 620));
        return reference;
    }
    private static DashboardBackdropView View()
    {
        var view = new DashboardBackdropView { HeroHeight = 480, Artwork = Picture(20) };
        view.Measure(new Size(980, 2400)); view.Arrange(new Rect(0, 0, 980, 2400)); return view;
    }
    private static Action<DrawingContext, bool, Rect?> Frame(DashboardBackdropView view) =>
        typeof(DashboardBackdropView).GetMethod("DrawFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<DrawingContext, bool, Rect?>>(view);
    private static BitmapSource Picture(byte shift)
    {
        var pixels = new byte[64 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = shift; pixels[i + 1] = 160; pixels[i + 2] = (byte)(255 - shift); pixels[i + 3] = 255; }
        var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4); image.Freeze(); return image;
    }
    private static void WithTheme(bool light, Action check)
    {
        var field = typeof(UiThemeService).GetField("_isLight", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ambient = typeof(UiThemeService).GetField("<AmbientEnabled>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldLight = field.GetValue(null); var oldAmbient = ambient.GetValue(null);
        try { field.SetValue(null, light); ambient.SetValue(null, true); check(); }
        finally { field.SetValue(null, oldLight); ambient.SetValue(null, oldAmbient); }
    }
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var worker = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
