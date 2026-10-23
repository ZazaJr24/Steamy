using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class BackdropFrameProbe
{
    internal static async Task RunAsync(Assembly assembly, string assemblyPath, string output)
    {
        var complete = new TaskCompletionSource<Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { complete.TrySetResult(Run(assembly)); }
            catch (Exception error) { complete.TrySetException(error); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        var report = await complete.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var result = new
        {
            scope = "Isolated STA recordings of the actual DashboardBackdropView from the supplied Release assembly, synthetic frozen artwork, no Application/Window/Steamy.exe startup, account data or network. Not real-app FPS, input latency, process RAM or compositor timing.",
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
            applicationCreated = false, windowCreated = false, portableLaunched = false,
            report.Phases, report.Pixels
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Report Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("Steamy.Controls.DashboardBackdropView", true)!;
        var theme = assembly.GetType("Steamy.Services.UiThemeService", true)!;
        var lightField = theme.GetField("_isLight", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ambientField = theme.GetField("<AmbientEnabled>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
        var phases = new List<Phase>(); var pixels = new List<PixelSample>();
        var regions = new[] { new Rect(180, 0, 540, 36), new Rect(180, 450, 540, 150), new Rect(180, 740, 540, 180), new Rect(80, 1650, 540, 140) };
        foreach (var light in new[] { false, true })
        foreach (var ambient in new[] { false, true })
        foreach (var preview in new[] { false, true })
        foreach (var crossfade in new[] { false, true })
        {
            if (preview && !ambient) continue;
            lightField.SetValue(null, light); ambientField.SetValue(null, ambient);
            var view = (FrameworkElement)Activator.CreateInstance(type)!;
            type.GetProperty("HeroHeight")!.SetValue(view, 480d);
            type.GetProperty("Artwork")!.SetValue(view, Picture(10));
            view.Measure(new Size(980, 2400)); view.Arrange(new Rect(0, 0, 980, 2400));
            type.GetField("_ambientBitmap", flags)!.SetValue(view, Picture(45, translucent: true));
            if (crossfade)
            {
                type.GetField("_previous", flags)!.SetValue(view, Picture(85));
                type.GetField("_previousAmbientBitmap", flags)!.SetValue(view, Picture(100, translucent: true));
                var blend = (DependencyProperty)type.GetField("BlendProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                view.SetValue(blend, .37d);
            }
            if (preview)
            {
                var brush = new ImageBrush(Picture(130, translucent: true)) { Stretch = Stretch.Fill }; brush.Freeze();
                type.GetField("_previewBrush", flags)!.SetValue(view, brush);
                type.GetField("_previewTop", flags)!.SetValue(view, 560d);
                var amount = (DependencyProperty)type.GetField("PreviewAmountProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                view.SetValue(amount, .8d);
            }
            var draw = type.GetMethod("DrawFrame", flags)!.CreateDelegate<Action<DrawingContext, bool, Rect?>>(view);
            DrawingVisual Frame(Rect? region)
            {
                var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using var context = visual.RenderOpen(); draw(context, true, region); return visual;
            }
            var full = Frame(null);
            for (var index = 0; index < regions.Length; index++)
            {
                var cropped = Frame(regions[index]);
                var bounds = VisualTreeHelper.GetDrawing(cropped).Bounds;
                pixels.Add(new(light, ambient, preview, crossfade, index, Hash(full, regions[index]), Hash(cropped, regions[index]), bounds.Width, bounds.Height));
            }
            if (!ambient || crossfade) continue;
            var recording = new DrawingVisual();
            void Record(int index)
            {
                using var context = recording.RenderOpen();
                draw(context, true, new Rect(0, 180 + index % 120, 980, 620));
            }
            for (var index = 0; index < 40; index++) Record(index);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 480; index++) Record(index);
            watch.Stop();
            phases.Add(new(light, preview, 480, GC.GetAllocatedBytesForCurrentThread() - before, watch.Elapsed.TotalMilliseconds, VisualTreeHelper.GetDrawing(recording).Bounds.Height));
            GC.KeepAlive(view);
        }
        return new(phases, pixels);
    }

    private static string Hash(DrawingVisual visual, Rect region)
    {
        var crop = new DrawingVisual();
        using (var context = crop.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-region.X, -region.Y));
            context.DrawDrawing(VisualTreeHelper.GetDrawing(visual)); context.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)region.Width, (int)region.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(crop);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static BitmapSource Picture(int shift, bool translucent = false)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var alpha = translucent ? 48 + (x * 7 + y * 11) % 208 : 255;
            pixels[i] = (byte)(((x + shift) % 256) * alpha / 255); pixels[i + 1] = (byte)(((y + shift) % 256) * alpha / 255);
            pixels[i + 2] = (byte)(((x + y + shift) % 256) * alpha / 255); pixels[i + 3] = (byte)alpha;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private sealed record Report(List<Phase> Phases, List<PixelSample> Pixels);
    private sealed record Phase(bool Light, bool Preview, int Frames, long AllocatedBytes, double ElapsedMs, double RecordedHeight);
    private sealed record PixelSample(bool Light, bool Ambient, bool Preview, bool Crossfade, int Region, string FullSha256, string CroppedSha256, double RecordedWidth, double RecordedHeight);
}
