using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>Reads an installed game's own high-resolution icon without executing it.</summary>
internal static class LocalGameArtwork
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static BitmapImage? Read(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            var folder = Path.GetDirectoryName(executable)!;
            // Prefer the publisher's packaged artwork over Windows' executable icon.
            foreach (var asset in new[] { "Square480x480Logo.png", "Square150x150Logo.png", "StoreLogo.png", "Square44x44Logo.png" })
            {
                var path = Path.Combine(folder, asset);
                if (!File.Exists(path)) continue;
                var packaged = new BitmapImage();
                packaged.BeginInit();
                packaged.CacheOption = BitmapCacheOption.OnLoad;
                packaged.DecodePixelWidth = 512;
                packaged.UriSource = new Uri(path);
                packaged.EndInit();
                packaged.Freeze();
                return packaged;
            }
            if (SHDefExtractIcon(executable, 0, 0, out large, out small, 256) != 0 || large == IntPtr.Zero) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) DestroyIcon(small);
        }
    }
}
