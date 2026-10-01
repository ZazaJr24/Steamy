using System.IO.Compression;
using Steamy.Services;

namespace Steamy.Tests;

public class ArchiveExtractorTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Zip_archives_are_extracted_with_their_folders()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "fix.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("bin/steam_api64.dll").Open())) writer.Write("dll");
            using (var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open())) writer.Write("hi");
        }

        var target = Path.Combine(temp.Path, "out");
        var result = ArchiveExtractor.Extract(zip, target);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.FilesWritten);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "bin", "steam_api64.dll")));
        Assert.False(result.UsedPassword);
    }

    [Fact]
    public void Seven_zip_archives_are_extracted()
    {
        using var temp = new TempFolder();
        var progress = new List<ArchiveExtractProgress>();

        var result = ArchiveExtractor.Extract(Fixture("fix.7z"), temp.Path, progress: new SyncProgress<ArchiveExtractProgress>(progress.Add));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("online fix readme\n", File.ReadAllText(Path.Combine(temp.Path, "fix", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "fix", "bin", "steam_api64.dll")));
        // Empty files are written too (gbe_fork's archive has several placeholder files).
        Assert.Equal(0, new FileInfo(Path.Combine(temp.Path, "fix", "empty.flag")).Length);
        Assert.False(result.UsedPassword);
        Assert.Equal(result.FilesWritten, progress.Count);
        Assert.All(progress, update => Assert.Equal(result.FilesWritten, update.Total));
    }

    [Fact]
    public void Password_protected_7z_needs_a_matching_password()
    {
        using var temp = new TempFolder();

        var withoutPassword = ArchiveExtractor.Extract(Fixture("fix-password.7z"), Path.Combine(temp.Path, "a"));
        var withPassword = ArchiveExtractor.Extract(Fixture("fix-password.7z"), Path.Combine(temp.Path, "b"),
            new[] { "wrong", "online-fix.me" });

        Assert.False(withoutPassword.Succeeded);
        Assert.Contains("password", withoutPassword.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(withPassword.Succeeded, withPassword.Message);
        Assert.True(withPassword.UsedPassword);
        Assert.Equal("online fix readme\n", File.ReadAllText(Path.Combine(temp.Path, "b", "fix", "readme.txt")));
    }

    [Theory]
    [InlineData("Rar5.solid.rar", null)]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test")]
    [InlineData("Rar5.multi.part01.rar", null)]
    public void Rar5_archives_are_extracted(string name, string? password)
    {
        using var temp = new TempFolder();

        var result = ArchiveExtractor.Extract(Fixture(name), temp.Path, password is null ? null : new[] { password });

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.FilesWritten > 0);
        Assert.NotEmpty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Files_that_are_not_archives_fail_with_a_message()
    {
        using var temp = new TempFolder();
        var file = temp.Write("notes.txt", "just text, not an archive");

        var result = ArchiveExtractor.Extract(file, Path.Combine(temp.Path, "out"));

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("a/../../evil.txt")]
    public void Entries_cannot_escape_the_target_folder(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "steamy-root") + Path.DirectorySeparatorChar;
        var destination = ArchiveExtractor.SafeDestination(root, key);
        Assert.True(destination is null || destination.StartsWith(root, StringComparison.Ordinal), destination);
    }

    [Fact]
    public void Zip_slip_entries_are_skipped()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open())) writer.Write("x");
            using (var writer = new StreamWriter(archive.CreateEntry("ok.txt").Open())) writer.Write("y");
        }

        var target = Path.Combine(temp.Path, "out");
        var result = ArchiveExtractor.Extract(zip, target);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.FilesWritten);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(target, "ok.txt")));
    }

    [Fact]
    public void Cancellation_during_one_entry_copy_stops_further_writes()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "large-entry.bin");
        File.WriteAllBytes(path, new byte[1024 * 1024]);
        using var cancellation = new CancellationTokenSource();
        using var destination = new CancellingWriteStream(cancellation);
        using (var guarded = new ArchiveExtractor.CancellationCheckingWriteStream(destination, cancellation.Token))
        using (var input = File.OpenRead(path))
        {
            // The destination cancels exactly at its first write, with no timers or races.
            // This is also the synchronous output path used by solid WriteEntryTo.
            Assert.ThrowsAny<OperationCanceledException>(() => input.CopyTo(guarded, 4096));
            Assert.Equal(1, destination.Writes);
            Assert.InRange(destination.Length, 1, new FileInfo(path).Length - 1);
            Assert.ThrowsAny<OperationCanceledException>(() => guarded.WriteByte(42));
            Assert.Equal(1, destination.Writes);
        }
        // The wrapper leaves the underlying output open for its owning caller.
        Assert.True(destination.CanWrite);
        destination.WriteByte(42);
    }

    [Fact]
    public async Task Cancellation_during_async_write_is_observed_before_success_is_reported()
    {
        using var cancellation = new CancellationTokenSource();
        using var destination = new CancellingWriteStream(cancellation);
        await using var guarded = new ArchiveExtractor.CancellationCheckingWriteStream(destination, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guarded.WriteAsync(new byte[32].AsMemory()).AsTask());
        Assert.Equal(1, destination.Writes);
        Assert.ThrowsAny<OperationCanceledException>(() => guarded.Write(new byte[32].AsSpan()));
        Assert.Equal(1, destination.Writes);
    }

    private sealed class CancellingWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public int Writes { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Writes++;
            cancellation.Cancel();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // MemoryStream's span overload falls back to the virtual array overload
            // for derived streams. Count the actual array write in one shared path.
            var bytes = buffer.ToArray();
            Write(bytes, 0, bytes.Length);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reports synchronously so the test can count updates without waiting on a context.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
