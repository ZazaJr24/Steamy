using System.IO;

namespace Steamy.Services;

/// <summary>
/// BitmapImage keeps its StreamSource property even with OnLoad. Disposing this
/// wrapper releases the compressed buffer after WIC has decoded the pixels.
/// </summary>
internal sealed class ReleasingImageStream(byte[] bytes) : Stream
{
    private MemoryStream? _content = new(bytes, false);
    private MemoryStream Content => _content ?? throw new ObjectDisposedException(nameof(ReleasingImageStream));
    public override bool CanRead => _content?.CanRead == true;
    public override bool CanSeek => _content?.CanSeek == true;
    public override bool CanWrite => false;
    public override long Length => Content.Length;
    public override long Position { get => Content.Position; set => Content.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Content.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Content.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => Content.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing) { _content?.Dispose(); _content = null; } base.Dispose(disposing); }
}
