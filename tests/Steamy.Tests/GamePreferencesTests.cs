using Steamy.Services;

namespace Steamy.Tests;

public sealed class GamePreferencesTests
{
    [Fact]
    public async Task NotesAndArgumentsSurviveRestartAndStaySeparateForSteamAndLocalGames()
    {
        using var directory = new TempFolder();
        var store = new GamePreferencesStore(directory.Path);
        await store.SaveAsync(1808500, new("Try the north extraction\nBring a shield", "-windowed"));
        await store.SaveAsync(-42, new("Local save slot 3", "--profile 3", @"C:\Games\Local\game.exe"));
        var reopened = new GamePreferencesStore(directory.Path);
        Assert.Equal(new("Try the north extraction\nBring a shield", "-windowed"), await reopened.ReadAsync(1808500));
        Assert.Equal(new("Local save slot 3", "--profile 3", @"C:\Games\Local\game.exe"), await reopened.ReadAsync(-42));
        Assert.Equal(new GamePreferences(), await reopened.ReadAsync(999));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public async Task CancelledSaveKeepsExistingNotesAndDoesNotLeaveTemporaryFiles()
    {
        using var directory = new TempFolder();
        var store = new GamePreferencesStore(directory.Path);
        await store.SaveAsync(42, new("Keep me"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(42, new("Replace me"), cancellation.Token));
        Assert.Equal("Keep me", (await store.ReadAsync(42)).Notes);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public async Task CorruptAndOversizedInputDoNotBlockBrowsing()
    {
        using var directory = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "42.json"), "{broken");
        var store = new GamePreferencesStore(directory.Path);
        Assert.Equal(new GamePreferences(), await store.ReadAsync(42));
        await store.SaveAsync(42, new(new string('x', 40000), "--option\0"));
        Assert.Equal(32000, (await store.ReadAsync(42)).Notes.Length);
        Assert.Equal("--option", (await store.ReadAsync(42)).LaunchArguments);
    }
}
