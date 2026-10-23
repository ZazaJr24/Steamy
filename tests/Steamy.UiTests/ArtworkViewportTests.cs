using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ArtworkViewportTests
{
    [Fact]
    public void EmptyImageUsesItsAllocatedSlotToStartTheFirstImageLoad()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var card = new Border { Width = 180, Height = 120, Child = image };
            var panel = new StackPanel(); panel.Children.Add(card); panel.Children.Add(new Border { Height = 1000 });
            var scroll = new ScrollViewer { Content = panel };
            var window = new Window { Width = 300, Height = 240, WindowStyle = WindowStyle.None, Content = scroll, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                Assert.Null(image.Source); Assert.Equal(new Size(), image.RenderSize);
                Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(image));
                scroll.ScrollToEnd(); window.UpdateLayout(); Pump();
                Assert.Null(ArtworkViewport.Priority(image));
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void PrioritizesVisibleImagesBoundsPrefetchAndDetachesTheScrollObserver()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var panel = new StackPanel();
            var cards = Enumerable.Range(0, 8).Select(_ => new Border { Width = 180, Height = 120 }).ToArray();
            foreach (var card in cards) panel.Children.Add(card);
            var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var window = new Window { Width = 300, Height = 240, WindowStyle = WindowStyle.None, Content = scroll, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(cards[0]));
                Assert.Equal(ImagePriority.Prefetch, ArtworkViewport.Priority(cards[3]));
                Assert.Null(ArtworkViewport.Priority(cards[7]));
                var calls = 0; var observer = ArtworkViewport.Observe(cards[0], () => calls++);
                scroll.ScrollToVerticalOffset(250); scroll.ScrollToVerticalOffset(500); window.UpdateLayout(); Pump();
                Assert.InRange(calls, 1, 3);
                scroll.ScrollToEnd(); window.UpdateLayout(); Pump();
                Assert.Null(ArtworkViewport.Priority(cards[0]));
                Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(cards[7]));
                var beforeMinimize = calls;
                window.WindowState = WindowState.Minimized; Pump();
                Assert.True(calls > beforeMinimize); Assert.Null(ArtworkViewport.Priority(cards[7]));
                window.WindowState = WindowState.Normal; window.UpdateLayout(); Pump();
                Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(cards[7]));
                observer.Dispose(); var before = calls;
                scroll.ScrollToTop(); window.UpdateLayout(); Pump(); Assert.Equal(before, calls);
                window.WindowState = WindowState.Minimized; Pump(); Assert.Equal(before, calls);
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20))); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void OffscreenArtworkBindingsStaySuspendedDuringLateSharedUpdatesAndRestoreTogether()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var first = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); first.Freeze();
                var second = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[8], 8); second.Freeze();
                var source = new Image { Source = first };
                var image = new Image(); var halo = new ArtworkHalo(); var trailer = new TrailerPreview(); var brush = new ImageBrush();
                foreach (var (owner, property) in new (DependencyObject, DependencyProperty)[]
                    { (image, Image.SourceProperty), (halo, ArtworkHalo.SourceProperty), (trailer, TrailerPreview.PosterProperty), (brush, ImageBrush.ImageSourceProperty) })
                    BindingOperations.SetBinding(owner, property, new Binding(nameof(Image.Source)) { Source = source });
                var panel = new StackPanel(); panel.Children.Add(image); panel.Children.Add(halo); panel.Children.Add(trailer); panel.Children.Add(new Border { Background = brush });
                Assert.Same(first, image.Source); Assert.Same(first, halo.Source); Assert.Same(first, trailer.Poster); Assert.Same(first, brush.ImageSource);
                ArtworkLease.SetImagesActive(panel, false);
                Assert.Null(image.Source); Assert.Null(halo.Source); Assert.Null(trailer.Poster); Assert.Null(brush.ImageSource);
                source.Source = second; Pump();
                Assert.Null(image.Source); Assert.Null(halo.Source); Assert.Null(trailer.Poster); Assert.Null(brush.ImageSource);
                ArtworkLease.SetImagesActive(panel, true);
                Assert.Same(second, image.Source); Assert.Same(second, halo.Source); Assert.Same(second, trailer.Poster); Assert.Same(second, brush.ImageSource);
                Assert.NotNull(BindingOperations.GetBindingExpression(image, Image.SourceProperty));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20))); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
