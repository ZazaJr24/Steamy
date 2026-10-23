using System.IO;
using System.Text;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class SteamShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Steamy-shortcut-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "userdata", "42", "config", "shortcuts.vdf");
    public SteamShortcutTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "steam.exe"), "Fixture only; never executed");
        File.WriteAllText(Path.Combine(_root, "config", "loginusers.vdf"), "\"users\" { \"76561197960265770\" { \"MostRecent\" \"1\" } }");
    }
    [Fact]
    public void CreatesQuotedUnicodeShortcutWithArgumentsAndSteamFields()
    {
        var exe = Path.Combine(_root, "My Game", "Spiel.exe");
        var result = SteamShortcutFile.Add(FilePath, "Spiel — 日本語", exe, "--profile \"Player One\"");
        Assert.True(result.Added); Assert.Null(result.BackupPath);
        var entry = ReadEntries(File.ReadAllBytes(FilePath)).Single();
        Assert.Equal("0", entry.Key);
        Assert.Equal("Spiel — 日本語", entry.Value["AppName"]);
        Assert.Equal("\"" + exe + "\"", entry.Value["Exe"]);
        Assert.Equal("\"" + Path.GetDirectoryName(exe) + "\"", entry.Value["StartDir"]);
        Assert.Equal("--profile \"Player One\"", entry.Value["LaunchOptions"]);
        Assert.Equal(1u, entry.Value["AllowOverlay"]);
        Assert.True(((uint)entry.Value["appid"] & 0x80000000) != 0);
    }
    [Fact]
    public void ExistingEntriesRemainByteIdenticalAreBackedUpAndAreNotDuplicated()
    {
        var first = Path.Combine(_root, "First.exe"); var second = Path.Combine(_root, "Second.exe");
        SteamShortcutFile.Add(FilePath, "First", first, "--original");
        var original = File.ReadAllBytes(FilePath);
        var added = SteamShortcutFile.Add(FilePath, "Second", second, "");
        Assert.NotNull(added.BackupPath); Assert.Equal(original, File.ReadAllBytes(added.BackupPath));
        var after = File.ReadAllBytes(FilePath);
        Assert.Equal(original[..^2], after[..(original.Length - 2)]);
        var entries = ReadEntries(after);
        Assert.Equal(2, entries.Count); Assert.Equal("--original", entries["0"]["LaunchOptions"]);
        Assert.Equal("Second", entries["1"]["AppName"]);
        var duplicate = SteamShortcutFile.Add(FilePath, "Different display name", second.ToUpperInvariant().Replace('\\', '/'), "--changed");
        Assert.False(duplicate.Added); Assert.Null(duplicate.BackupPath);
        Assert.Equal(after, File.ReadAllBytes(FilePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.bak"));
    }
    [Theory]
    [InlineData(new byte[] { 0, 115, 104, 111, 114, 116, 99, 117, 116, 115, 0, 8, 8, 42 })]
    [InlineData(new byte[] { 0, 115, 0, 8, 8 })]
    [InlineData(new byte[] { 0, 115, 104, 111, 114, 116, 99, 117, 116, 115, 0, 0, 48, 0, 42, 120, 0, 8, 8, 8 })]
    [InlineData(new byte[] { 0, 115, 104, 111, 114, 116, 99, 117, 116, 115, 0, 0, 48 })]
    public void RefusesMalformedOrUnknownFormatsWithoutChangingTheOriginal(byte[] original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllBytes(FilePath, original);
        Assert.Throws<InvalidDataException>(() => SteamShortcutFile.Add(FilePath, "Game", Path.Combine(_root, "game.exe"), ""));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }
    [Fact]
    public void PreservesUnknownNestedFieldsAndAllocatesAfterExistingIndexHoles()
    {
        SteamShortcutFile.Add(FilePath, "First", Path.Combine(_root, "First.exe"), "");
        var source = File.ReadAllBytes(FilePath); source[12] = (byte)'9';
        byte[] extension = [0, .. Encoding.UTF8.GetBytes("FutureData"), 0, 1, .. Encoding.UTF8.GetBytes("CustomField"), 0, .. Encoding.UTF8.GetBytes("Keep me exactly"), 0, 8];
        byte[] original = [.. source[..^3], .. extension, .. source[^3..]];
        File.WriteAllBytes(FilePath, original);
        var added = SteamShortcutFile.Add(FilePath, "Second", Path.Combine(_root, "Second.exe"), "");
        var output = File.ReadAllBytes(FilePath);
        Assert.Equal(original[..^2], output[..(original.Length - 2)]);
        Assert.Equal(original, File.ReadAllBytes(added.BackupPath!));
        Assert.Equal(new byte[] { 0, (byte)'1', (byte)'0', 0 }, output[(original.Length - 2)..(original.Length + 2)]);
    }
    [Fact]
    public async Task RunningSteamRequiresAnExplicitRestartThenAddsTheActualExecutable()
    {
        var client = new Client { IsRunning = true };
        var service = new SteamShortcutService(new Settings(_root), client);
        var game = InstalledGame();
        var first = await service.AddAsync(game, game.LaunchExecutable, "--test");
        Assert.False(first.Succeeded); Assert.True(first.RequiresSteamRestart);
        Assert.Equal(0, client.ExitRequests); Assert.False(File.Exists(FilePath));
        var added = await service.AddAsync(game, game.LaunchExecutable, "--test", restartSteam: true);
        Assert.True(added.Succeeded); Assert.Equal(1, client.ExitRequests); Assert.Equal(1, client.OpenRequests);
        Assert.Equal("--test", ReadEntries(File.ReadAllBytes(FilePath))["0"]["LaunchOptions"]);
    }
    [Fact]
    public async Task FailedSavePreservesShortcutsAndReopensSteamAfterRequestedExit()
    {
        var client = new Client { IsRunning = true };
        var service = new SteamShortcutService(new Settings(_root), client);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllBytes(FilePath, [42, 0, 8]);
        var game = InstalledGame();
        var result = await service.AddAsync(game, game.LaunchExecutable, "", restartSteam: true);
        Assert.False(result.Succeeded); Assert.Equal(new byte[] { 42, 0, 8 }, File.ReadAllBytes(FilePath));
        Assert.Equal(1, client.OpenRequests);
    }
    [Fact]
    public async Task RejectsAnExecutableOutsideTheGameAndUninstalledGames()
    {
        var client = new Client(); var service = new SteamShortcutService(new Settings(_root), client);
        var game = InstalledGame();
        var outside = await service.AddAsync(game, Path.Combine(_root, "steam.exe"), "");
        var uninstalled = await service.AddAsync(new Game { IsLocalDownload = true }, game.LaunchExecutable, "");
        Assert.False(outside.Succeeded); Assert.False(uninstalled.Succeeded);
        Assert.False(File.Exists(FilePath)); Assert.Equal(0, client.ExitRequests);
    }
    [Fact]
    public async Task RegisteredSteamGamesDoNotNeedAnExecutableOrCreateAnotherShortcut()
    {
        var client = new Client { IsRunning = true }; var service = new SteamShortcutService(new Settings(_root), client);
        var game = new Game { AppId = 284160, Name = "BeamNG.drive", InstallState = GameInstallState.Installed };
        var result = await service.AddAsync(game, "", "");
        Assert.True(result.Succeeded); Assert.Contains("already registered", result.Message);
        Assert.False(result.RequiresSteamRestart); Assert.Equal(0, client.ExitRequests); Assert.Equal(0, client.OpenRequests);
        Assert.False(File.Exists(FilePath));
    }
    [Fact]
    public async Task LeavingDuringAnApprovedShutdownStillReopensSteamWithoutWritingTheShortcut()
    {
        var client = new ClosingClient(); var service = new SteamShortcutService(new Settings(_root), client);
        var game = InstalledGame(); using var cancellation = new CancellationTokenSource();
        var addition = service.AddAsync(game, game.LaunchExecutable, "", restartSteam: true, token: cancellation.Token);
        await client.ExitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); Assert.False(addition.IsCompleted);
        client.Running = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addition);
        Assert.Equal(1, client.OpenRequests); Assert.False(File.Exists(FilePath));
    }
    [Fact]
    public void ChoosesTheMostRecentLoginAndNeverGuessesAmongSeveralAccounts()
    {
        File.WriteAllText(Path.Combine(_root, "config", "loginusers.vdf"), "\"users\" { \"76561197960265771\" { \"MostRecent\" \"0\" \"Timestamp\" \"900\" } \"76561197960265770\" { \"MostRecent\" \"1\" \"Timestamp\" \"10\" } }");
        Assert.Equal("42", SteamShortcutService.FindAccount(_root));
        File.Delete(Path.Combine(_root, "config", "loginusers.vdf"));
        Directory.CreateDirectory(Path.Combine(_root, "userdata", "42"));
        Directory.CreateDirectory(Path.Combine(_root, "userdata", "43"));
        Assert.Null(SteamShortcutService.FindAccount(_root));
    }
    private Game InstalledGame()
    {
        var folder = Path.Combine(_root, "Game"); Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "Game.exe"); File.WriteAllText(exe, "Fixture only; never executed");
        return new Game { AppId = -42, Name = "Local game", IsLocalDownload = true, InstallState = GameInstallState.Installed, InstallFolder = folder, LaunchExecutable = exe };
    }
    // Independent reader of the newly generated subset: every field and terminator must be valid.
    private static Dictionary<string, Dictionary<string, object>> ReadEntries(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        string Text() { var bytes = new List<byte>(); byte value; while ((value = reader.ReadByte()) != 0) bytes.Add(value); return Encoding.UTF8.GetString(bytes.ToArray()); }
        Assert.Equal(0, reader.ReadByte()); Assert.Equal("shortcuts", Text());
        var entries = new Dictionary<string, Dictionary<string, object>>();
        byte type;
        while ((type = reader.ReadByte()) != 8)
        {
            Assert.Equal(0, type); var key = Text(); var fields = new Dictionary<string, object>();
            while ((type = reader.ReadByte()) != 8)
            {
                var field = Text();
                if (type == 0) { Assert.Equal("tags", field); Assert.Equal(8, reader.ReadByte()); }
                else fields[field] = type switch { 1 => Text(), 2 => reader.ReadUInt32(), _ => throw new InvalidDataException("Unexpected generated field") };
            }
            entries.Add(key, fields);
        }
        Assert.Equal(8, reader.ReadByte()); Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        return entries;
    }
    private sealed class Settings(string root) : ISettingsService
    {
        public AppSettings Load() => new() { SteamLibraryPath = root };
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Client : ISteamShortcutClient
    {
        public bool IsRunning { get; set; }
        public int ExitRequests, OpenRequests;
        public void RequestExit(string root) { ExitRequests++; IsRunning = false; }
        public void Open(string root) { OpenRequests++; IsRunning = true; }
    }
    private sealed class ClosingClient : ISteamShortcutClient
    {
        public volatile bool Running = true;
        public bool IsRunning => Running;
        public int OpenRequests;
        public TaskCompletionSource ExitRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RequestExit(string root) => ExitRequested.TrySetResult();
        public void Open(string root) { OpenRequests++; Running = true; }
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
