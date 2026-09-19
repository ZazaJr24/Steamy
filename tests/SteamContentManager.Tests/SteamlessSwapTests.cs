using System.IO;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The Steamless workflow renames real files, so the swapping half is tested against real
/// temporary files: original becomes .bak, the unpacked build takes the original name, and undo
/// reverses exactly that.
/// </summary>
public sealed class SteamlessSwapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"steamless-test-{Guid.NewGuid():N}");

    public SteamlessSwapTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The naming Steamless really uses. A real run reported its result as
    /// <c>...\eurotrucks2.exe.unpacked.exe</c>, so the whole file name gets <c>.unpacked</c>
    /// appended. Assuming <c>game.unpacked.exe</c> made a successful unpack look like a failure.
    /// </summary>
    [Fact]
    public void TheUnpackedNameMatchesWhatSteamlessActuallyWrites()
    {
        var target = Path.Combine(_directory, "game.exe");

        Assert.Equal(Path.Combine(_directory, "game.exe.unpacked.exe"), SteamlessFileSwap.UnpackedPathFor(target));
        Assert.Equal(Path.Combine(_directory, "game.unpacked.exe"), SteamlessFileSwap.LegacyUnpackedPathFor(target));
    }

    [Fact]
    public void ApplyUnpackedResult_KeepsTheOriginalAsBackupAndMakesTheUnpackedBuildTheRealOne()
    {
        var target = Write("game.exe", "original");
        var unpacked = Write("game.exe.unpacked.exe", "unpacked");

        var applied = SteamlessFileSwap.TryApplyUnpackedResult(target, out var message);

        Assert.True(applied, message);
        Assert.False(File.Exists(unpacked));
        Assert.Equal("unpacked", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(SteamlessFileSwap.BackupPathFor(target)));
    }

    /// <summary>A leftover from an older Steamless build must still be picked up, not ignored.</summary>
    [Fact]
    public void ApplyUnpackedResult_AlsoAcceptsTheNameOlderBuildsUse()
    {
        var target = Write("game.exe", "original");
        var unpacked = Write("game.unpacked.exe", "unpacked");

        var applied = SteamlessFileSwap.TryApplyUnpackedResult(target, out var message);

        Assert.True(applied, message);
        Assert.False(File.Exists(unpacked));
        Assert.Equal("unpacked", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(SteamlessFileSwap.BackupPathFor(target)));
    }

    [Fact]
    public void ApplyUnpackedResult_RefusesWhenSteamlessProducedNothing()
    {
        var target = Write("game.exe", "original");

        var applied = SteamlessFileSwap.TryApplyUnpackedResult(target, out var message);

        Assert.False(applied);
        Assert.Contains("no unpacked file", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.False(File.Exists(SteamlessFileSwap.BackupPathFor(target)));
    }

    [Fact]
    public void Undo_PutsTheOriginalBackAndKeepsTheCurrentBuildAside()
    {
        var target = Write("game.exe", "original");
        Write("game.exe.unpacked.exe", "unpacked");
        Assert.True(SteamlessFileSwap.TryApplyUnpackedResult(target, out _));

        var undone = SteamlessFileSwap.TryUndo(target, out var message);

        Assert.True(undone, message);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("unpacked", File.ReadAllText(SteamlessFileSwap.UnpackedPathFor(target)));
        Assert.False(File.Exists(SteamlessFileSwap.BackupPathFor(target)));
    }

    [Fact]
    public void ApplyUnpackedResult_DoesNotDestroyAnEarlierBackup()
    {
        var target = Write("game.exe", "original");
        Write("game.bak.exe", "earlier-backup");
        Write("game.exe.unpacked.exe", "unpacked");

        var applied = SteamlessFileSwap.TryApplyUnpackedResult(target, out var message);

        Assert.True(applied, message);
        Assert.Equal("original", File.ReadAllText(SteamlessFileSwap.BackupPathFor(target)));
        Assert.True(File.Exists(Path.Combine(_directory, "game.bak-2.exe")));
        Assert.Equal("earlier-backup", File.ReadAllText(Path.Combine(_directory, "game.bak-2.exe")));
    }

    [Fact]
    public void Undo_ReportsHonestlyWhenThereIsNoBackup()
    {
        var target = Write("game.exe", "original");

        var undone = SteamlessFileSwap.TryUndo(target, out var message);

        Assert.False(undone);
        Assert.Contains("No backup", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(target));
    }

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
