using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckGifRuntime()
    {
        _phase = "GIF decode/playback/visibility";
        var encoder = new GifBitmapEncoder();
        foreach (var color in new[] { Colors.Red, Colors.Blue })
        {
            var pixels = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { color.B, color.G, color.R, 255 }).ToArray();
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
            var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)10);
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        }
        using var stream = new MemoryStream(); encoder.Save(stream); var bytes = stream.ToArray();
        Assert.Equal(2, ChatMediaLoader.ValidateGif(bytes));
        Assert.Throws<IOException>(() => ChatMediaLoader.ValidateGif(bytes.AsSpan(0, 14)));
        var tooLarge = bytes.ToArray(); tooLarge[6] = 0xff; tooLarge[7] = 0x7f;
        Assert.Throws<IOException>(() => ChatMediaLoader.ValidateGif(tooLarge));
        var previousLoader = ChatImagePreview.MediaLoader; var previousMotion = MotionPreferences.ReduceEffects;
        var previousActivity = ChatImagePreview.WindowIsActive; var active = true;
        // The desktop can deny foreground activation to an automated process.
        // Exercise both activity states explicitly while retaining real WPF geometry.
        ChatImagePreview.WindowIsActive = _ => active;
        ChatImagePreview.MediaLoader = (_, token) => Task.Run(() => ChatMediaLoader.Decode(bytes), token);
        var preview = new ChatImagePreview { Url = "https://static.klipy.com/test.gif" };
        var stack = new StackPanel(); stack.Children.Add(preview); stack.Children.Add(new Border { Height = 1200 });
        var scroll = new ScrollViewer { Content = stack };
        var window = new Window { Width = 480, Height = 400, Content = scroll, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
        try
        {
            MotionPreferences.Configure(false); window.Show(); PumpUntil(() => preview.HasMedia);
            PumpUntil(() => preview.IsPlaying);
            var image = Descendants<Image>(preview).First(); var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(image);
            Assert.Equal(2, animator.FrameCount);
            var frame = animator.CurrentFrameIndex;
            PumpUntil(() => animator.CurrentFrameIndex != frame);
            active = false; ChatImagePreview.RefreshVisibleMedia(); Assert.False(preview.IsPlaying);
            active = true; PumpUntil(() => preview.IsPlaying);
            scroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(240)); Assert.False(preview.IsPlaying);
            scroll.ScrollToHome(); PumpUntil(() => preview.IsPlaying);
            window.WindowState = WindowState.Minimized; PumpDispatcher(TimeSpan.FromMilliseconds(240)); Assert.False(preview.IsPlaying);
            window.WindowState = WindowState.Normal; MotionPreferences.Configure(true); PumpDispatcher(TimeSpan.FromMilliseconds(240)); Assert.False(preview.IsPlaying);
            var play = Descendants<Button>(preview).First(button => button.Content?.ToString()?.StartsWith("GIF") == true);
            play.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); PumpUntil(() => preview.IsPlaying);
            scroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(2600));
            Assert.False(preview.HasMedia); Assert.Null(preview.Child); Assert.Null(preview.Cursor);
            Assert.Null(image.Source); Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(image));
            TracePhase("GIF reload after offscreen media release");
            scroll.ScrollToHome(); window.UpdateLayout(); PumpUntil(() => preview.HasMedia);
            window.Content = null; PumpDispatcher(TimeSpan.FromMilliseconds(100)); Assert.False(preview.HasMedia);
            Assert.Null(preview.Child); Assert.Null(preview.Cursor);

            MotionPreferences.Configure(false);
            var previews = Enumerable.Range(0, 6).Select(index => new ChatImagePreview
            { Url = $"https://static.klipy.com/test-{index}.gif", Width = 115, MinWidth = 0, Height = 104, Margin = new(6) }).ToArray();
            var tiles = new WrapPanel(); foreach (var item in previews) tiles.Children.Add(item);
            window.Width = 720; window.Height = 420; window.Content = tiles;
            PumpUntil(() => previews.All(item => item.HasMedia)); ChatImagePreview.RefreshVisibleMedia();
            Assert.Equal(4, previews.Count(item => item.IsPlaying));
            window.WindowState = WindowState.Minimized; ChatImagePreview.RefreshVisibleMedia();
            Assert.DoesNotContain(previews, item => item.IsPlaying);
            window.WindowState = WindowState.Normal; PumpUntil(() => previews.Count(item => item.IsPlaying) == 4);
            window.Content = null; PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.DoesNotContain(previews, item => item.HasMedia);

            var requested = false; var cancelled = false;
            ChatImagePreview.MediaLoader = (_, token) =>
            {
                requested = true;
                var pending = new TaskCompletionSource<ChatMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => { cancelled = true; pending.TrySetCanceled(token); });
                return pending.Task;
            };
            var slow = new ChatImagePreview { Url = "https://static.klipy.com/slow.gif" };
            var slowStack = new StackPanel(); slowStack.Children.Add(slow); slowStack.Children.Add(new Border { Height = 1200 });
            var slowScroll = new ScrollViewer { Content = slowStack }; window.Content = slowScroll;
            PumpUntil(() => requested); slowScroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(cancelled); Assert.False(slow.HasMedia);
            window.Close();
        }
        finally { window.Close(); ChatImagePreview.MediaLoader = previousLoader; ChatImagePreview.WindowIsActive = previousActivity; MotionPreferences.Configure(previousMotion); }
    }
}
