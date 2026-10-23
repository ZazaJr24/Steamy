using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class BackdropRegionTests
{
    [Fact]
    public void ScrollJumpsAndResizeKeepTheWholeViewportPaintedWithBoundedDrawingData()
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            var view = new DashboardBackdropView { Height = 2400, HeroHeight = 480, Artwork = Picture(0) };
            var scroll = new System.Windows.Controls.ScrollViewer { Content = view, CanContentScroll = false,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled };
            var window = new Window { Content = scroll, Width = 980, Height = 620, ShowInTaskbar = false };
            void Pump()
            {
                window.UpdateLayout();
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            try
            {
                window.Show(); Pump();
                foreach (var height in new[] { 620d, 800d })
                {
                    window.Height = height; Pump();
                    foreach (var offset in new[] { 0d, 1100d, 1800d, 250d, 0d })
                    {
                        scroll.ScrollToVerticalOffset(offset); Pump();
                        var drawing = VisualTreeHelper.GetDrawing(view);
                        Assert.NotNull(drawing);
                        Assert.InRange(drawing.Bounds.Height, scroll.ViewportHeight - 1, scroll.ViewportHeight + 1);
                        Assert.InRange(drawing.Bounds.Top, scroll.VerticalOffset - 1, scroll.VerticalOffset + 1);
                        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
                        var pixels = Pixels(visual, new Rect(0, scroll.VerticalOffset, Math.Floor(view.ActualWidth), Math.Floor(scroll.ViewportHeight)));
                        for (var i = 3; i < pixels.Length; i += 4) Assert.Equal(255, pixels[i]);
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); Pump(); }
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void GlassRegionsPreserveFullBackdropPixelsWithoutRecordingTheFullPage(bool light, bool ambient, bool preview)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var lightField = typeof(UiThemeService).GetField("_isLight", BindingFlags.NonPublic | BindingFlags.Static)!;
            var ambientField = typeof(UiThemeService).GetField("<AmbientEnabled>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldLight = lightField.GetValue(null); var oldAmbient = ambientField.GetValue(null);
            try
            {
                lightField.SetValue(null, light); ambientField.SetValue(null, ambient);
                var view = new DashboardBackdropView { HeroHeight = 480, Artwork = Picture(0) };
                view.Measure(new Size(960, 1800)); view.Arrange(new Rect(0, 0, 960, 1800));
                if (preview)
                {
                    typeof(DashboardBackdropView).GetField("_previewBrush", flags)!.SetValue(view, new ImageBrush(Picture(30)) { Stretch = Stretch.Fill });
                    typeof(DashboardBackdropView).GetField("_previewTop", flags)!.SetValue(view, 560d);
                    var amount = (DependencyProperty)typeof(DashboardBackdropView).GetField("PreviewAmountProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                    view.SetValue(amount, .8d);
                }
                var draw = typeof(DashboardBackdropView).GetMethod("DrawFrame", flags)!;
                DrawingVisual Frame(Rect? region)
                {
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen()) draw.Invoke(view, [dc, true, region]);
                    return visual;
                }
                foreach (var crossfade in new[] { false, true })
                {
                    typeof(DashboardBackdropView).GetField("_previous", flags)!.SetValue(view, crossfade ? Picture(83) : null);
                    var blend = (DependencyProperty)typeof(DashboardBackdropView).GetField("BlendProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                    view.SetValue(blend, .37d);
                    var full = Frame(null);
                    foreach (var region in new[] { new Rect(220, 0, 540, 36), new Rect(180, 450, 540, 150), new Rect(320, 1050, 420, 220), new Rect(12, 1650, 500, 140) })
                    {
                        var cropped = Frame(region);
                        var recorded = VisualTreeHelper.GetDrawing(cropped).Bounds;
                        Assert.True(region.Contains(recorded), $"Recorded {recorded} outside glass region {region}.");
                        var expected = Pixels(full, region); var actual = Pixels(cropped, region);
                        Assert.Equal(expected.Length, actual.Length);
                        var maximumError = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
                        Assert.InRange(maximumError, 0, 2);
                    }
                    Assert.True((VisualTreeHelper.GetDrawing(Frame(new Rect(0, 1900, 100, 36)))?.Bounds ?? Rect.Empty).IsEmpty);
                }
            }
            catch (Exception error) { failure = error; }
            finally { lightField.SetValue(null, oldLight); ambientField.SetValue(null, oldAmbient); }
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static BitmapSource Picture(int shift)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)((x + shift) % 256); pixels[i + 1] = (byte)((y + shift) % 256);
            pixels[i + 2] = (byte)((x + y + shift) % 256); pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }

    private static byte[] Pixels(Visual visual, Rect region)
    {
        var crop = new DrawingVisual();
        using (var dc = crop.RenderOpen())
            dc.DrawRectangle(new VisualBrush(visual) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill }, null, new Rect(0, 0, region.Width, region.Height));
        var bitmap = new RenderTargetBitmap((int)region.Width, (int)region.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(crop);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
