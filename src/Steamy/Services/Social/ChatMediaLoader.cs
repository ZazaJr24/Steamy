using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

internal sealed record ChatMedia(byte[] Bytes, BitmapSource Poster, bool Animated);

/// <summary>Bounded downloads and frozen poster decoding outside WPF's dispatcher. No disk cache.</summary>
internal static class ChatMediaLoader
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal static Task<ChatMedia> LoadAsync(Uri uri, CancellationToken token) => LoadAsync(uri, token, 960);
    internal static async Task<ChatMedia> LoadAsync(Uri uri, CancellationToken token, int decodeWidth)
    {
        if (!ChatMediaPolicy.IsImageUri(uri)) throw new IOException("Unsupported image link.");
        var bytes = await ImagePipeline.Shared.LoadTransientContentAsync(uri, token).ConfigureAwait(false)
            ?? throw new IOException("The image is too large or unavailable.");
        return await Task.Run(() => Decode(bytes, decodeWidth), token).ConfigureAwait(false);
    }
    internal static ChatMedia Decode(byte[] bytes) => Decode(bytes, 960);
    private static ChatMedia Decode(byte[] bytes, int decodeWidth)
    {
        if (bytes.Length > MaxBytes) throw new IOException("Images can be up to 8 MB.");
        var animated = bytes.Length >= 6 && bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || bytes.Length >= 6 && bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8);
        if (animated) ValidateGif(bytes);
        using var memory = new ReleasingImageStream(bytes);
        var metadata = BitmapDecoder.Create(memory, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if ((long)metadata.PixelWidth * metadata.PixelHeight > 40_000_000 || metadata.PixelWidth > 32768 || metadata.PixelHeight > 32768) throw new IOException("Image dimensions are too large.");
        memory.Position = 0;
        var poster = new BitmapImage(); poster.BeginInit(); poster.CacheOption = BitmapCacheOption.OnLoad;
        var scale = Math.Min(1, Math.Clamp(decodeWidth, 1, 1920) / (double)Math.Max(metadata.PixelWidth, metadata.PixelHeight));
        poster.DecodePixelWidth = Math.Max(1, (int)(metadata.PixelWidth * scale)); poster.StreamSource = memory; poster.EndInit(); poster.Freeze();
        return new(bytes, poster, animated);
    }
    // Scan GIF blocks before giving compressed data to the animator; never expand every frame into memory.
    internal static int ValidateGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14) throw new IOException("Incomplete GIF.");
        var width = Read16(bytes, 6); var height = Read16(bytes, 8);
        if (width is <= 0 or > 1536 || height is <= 0 or > 1536 || (long)width * height > 1_500_000) throw new IOException("This GIF exceeds the playback size limit.");
        var at = 13 + ((bytes[10] & 0x80) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        var frames = 0; long pixels = 0;
        while (at < bytes.Length)
        {
            var marker = bytes[at++];
            if (marker == 0x3b) { if (frames == 0) throw new IOException("Empty GIF."); return frames; }
            if (marker == 0x21) { if (at >= bytes.Length) break; at++; }
            else if (marker == 0x2c)
            {
                if (at + 9 > bytes.Length) break;
                var frameWidth = Read16(bytes, at + 4); var frameHeight = Read16(bytes, at + 6);
                if (frameWidth <= 0 || frameHeight <= 0 || Read16(bytes, at) + frameWidth > width || Read16(bytes, at + 2) + frameHeight > height)
                    throw new IOException("Invalid GIF dimensions.");
                pixels += (long)frameWidth * frameHeight;
                if (++frames > 300 || pixels > 120_000_000) throw new IOException("This GIF exceeds the playback resource limit.");
                var packed = bytes[at + 8]; at += 9;
                if ((packed & 0x80) != 0) at += 3 * (1 << ((packed & 7) + 1));
                at++; // LZW minimum code size
            }
            else throw new IOException("Unsupported GIF data.");
            var terminated = false;
            while (at < bytes.Length)
            {
                var length = bytes[at++]; if (length == 0) { terminated = true; break; }
                at += length;
            }
            if (!terminated || at > bytes.Length) break;
        }
        throw new IOException("Incomplete GIF.");
    }
    private static int Read16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | bytes[at + 1] << 8;
}
