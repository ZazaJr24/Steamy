using Steamy.Services;

namespace Steamy.Tests;

public sealed class DepotResumeStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steamy-resume-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SnapshotKeepsExactManifestAndTarget()
    {
        var target = Path.Combine(_root, "game with spaces ü");
        var folder = DepotResumeStateStore.SessionDirectory(_root, 42, target);
        var state = new DepotResumeState(42, target, [new(43, "18446744073709551615"), new(44, "777")]);
        await DepotResumeStateStore.WriteAsync(folder, state, default);

        var restored = DepotResumeStateStore.Read(folder, 42, target + Path.DirectorySeparatorChar);
        Assert.NotNull(restored);
        Assert.Equal(state.Depots, restored.Depots);
        Assert.Null(DepotResumeStateStore.Read(folder, 42, Path.Combine(_root, "another game")));
        Assert.Null(DepotResumeStateStore.Read(folder, 99, target));
    }

    [Fact]
    public void TargetsAndAppsHaveSeparateCaches()
    {
        var a = DepotResumeStateStore.SessionDirectory(_root, 42, Path.Combine(_root, "a"));
        Assert.NotEqual(a, DepotResumeStateStore.SessionDirectory(_root, 42, Path.Combine(_root, "b")));
        Assert.NotEqual(a, DepotResumeStateStore.SessionDirectory(_root, 43, Path.Combine(_root, "a")));
        Assert.Equal(a, DepotResumeStateStore.SessionDirectory(_root, 42, Path.Combine(_root, "a", ".")));
    }

    [Fact]
    public async Task DuplicateDepotVersionsCannotBecomeAResumeSession()
    {
        var state = new DepotResumeState(42, _root, [new(43, "123"), new(43, "456")]);
        await Assert.ThrowsAsync<ArgumentException>(() => DepotResumeStateStore.WriteAsync(_root, state, default));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"AppId\":42,\"TargetFolder\":\"/tmp\",\"Depots\":null}")]
    public void BrokenStateIsNotTrusted(string contents)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "resume.json"), contents);
        Assert.Null(DepotResumeStateStore.Read(_root, 42, _root));
    }

    [Fact]
    public async Task CanceledWriteKeepsPreviousState()
    {
        var original = new DepotResumeState(42, _root, [new(43, "123")]);
        await DepotResumeStateStore.WriteAsync(_root, original, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DepotResumeStateStore.WriteAsync(
            _root, original with { Depots = [new(43, "456")] }, cancellation.Token));
        Assert.Equal("123", Assert.Single(DepotResumeStateStore.Read(_root, 42, _root)!.Depots).ManifestId);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
