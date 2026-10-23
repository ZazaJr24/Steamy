using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class AmbientProcessingTests
{
    [Fact]
    public void ColdBackdropProcessingKeepsItsPixelsAndRecordsUiThreadCost()
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var fixture = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
                BitmapSource source;
                if (fixture is not null && File.Exists(Path.Combine(fixture, "1174180_hero.jpg")))
                {
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 1600; image.UriSource = new Uri(Path.Combine(fixture, "1174180_hero.jpg")); image.EndInit(); image.Freeze(); source = image;
                }
                else
                {
                    var pixels = new byte[640 * 220 * 4];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i % 251); pixels[i + 1] = (byte)(i % 97); pixels[i + 2] = (byte)(i % 199); pixels[i + 3] = 255; }
                    source = BitmapSource.Create(640, 220, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4); source.Freeze();
                }
                var timings = new List<double>(); byte[]? output = null;
                for (var run = 0; run < 21; run++)
                {
                    var copy = source.Clone(); copy.Freeze();
                    var start = Stopwatch.GetTimestamp(); var result = GameDetailsAmbient.For(copy, 3.1);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Assert.True(result.IsFrozen);
                    if (run > 0) timings.Add(elapsed); // Exclude the first JIT/table warmup from the uncached median.
                    if (run == 0)
                    {
                        output = new byte[result.PixelWidth * result.PixelHeight * 4]; result.CopyPixels(output, result.PixelWidth * 4, 0);
                    }
                }
                if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "ambient-pixels.bgra"), output!);
                    File.WriteAllText(Path.Combine(folder, "ambient-processing.json"), JsonSerializer.Serialize(new
                    {
                        scope = "Isolated STA/UI-thread synchronous backdrop creation; 20 uncached source identities after warmup, not full-app frame latency.",
                        sourceWidth = source.PixelWidth, sourceHeight = source.PixelHeight, timesMs = timings,
                        medianMs = timings.Order().ElementAt(timings.Count / 2), p95Ms = timings.Order().ElementAt(18),
                        pixelsSha256 = Convert.ToHexString(SHA256.HashData(output!))
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(45)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
