using Steamy.ControllerProtocol;
using Steamy.Models;
using Steamy.Playtime;
using Steamy.Services;

namespace Steamy.UiTests;
public sealed class ControllerProfileTests
{
    [Fact]
    public async Task GameProfilesApplyBeforeLaunchAndReturnToTheUsersDefaultAfterExitOrStaleTracking()
    {
        var backend = new Backend(); var settings = new Settings(); var time = new Clock(); var playtime = new Times(time);
        using var service = new ControllerGameProfiles(backend, settings, playtime, time);
        await service.PrepareLaunchAsync(42); Assert.Equal("Shooter", backend.Applied.Last());
        playtime.Running = 42; await service.RefreshAsync();
        playtime.Running = 0; await service.RefreshAsync(); Assert.Equal("My default", backend.Applied.Last());
        playtime.Running = 43; await service.RefreshAsync(); Assert.Equal("Racing", backend.Applied.Last());
        backend.Running = false; await service.RefreshAsync(); backend.Running = true; await service.RefreshAsync();
        Assert.Equal(2, backend.Applied.Count(name => name == "Racing"));
        playtime.Stale = true; await service.RefreshAsync(); Assert.Equal("My default", backend.Applied.Last());
        playtime.Stale = false; playtime.Running = 44; await service.RefreshAsync();
        Assert.Equal("My default", backend.Applied.Last()); // A deleted assignment falls back to the user's default.
    }
    [Fact]
    public async Task AbandonedLaunchDoesNotKeepAGameProfileForever()
    {
        var backend = new Backend(); var settings = new Settings(); var time = new Clock(); var playtime = new Times(time);
        using var service = new ControllerGameProfiles(backend, settings, playtime, time);
        await service.PrepareLaunchAsync(42); await service.RefreshAsync(); Assert.Equal("Shooter", backend.Applied.Last());
        time.Now = time.Now.AddSeconds(121); await service.RefreshAsync(); Assert.Equal("My default", backend.Applied.Last());
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Settings : ISettingsService
    {
        private readonly AppSettings _options = new() { ControllerDefaultProfile = "My default", ControllerProfiles = [new("My default"), new("Shooter"), new("Racing")], ControllerGameProfiles = new() { [42] = "Shooter", [43] = "Racing", [44] = "Deleted" } };
        public AppSettings Load() => _options;
        public Task SaveAsync(AppSettings options, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Times(Clock clock) : IPlayTimeService
    {
        public int Running; public bool Stale;
        public Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(new PlayTimeSnapshot(1, Stale ? clock.Now.AddSeconds(-30) : clock.Now, Running == 0 ? [] : [new(Running, 0, true)]));
        public Task ConfigureAsync(AppSettings options, IEnumerable<Game> games) => Task.CompletedTask;
    }
    private sealed class Backend : IControllerBackend
    {
        public bool Running = true; public List<string> Applied = [];
        public bool IsInstalled => true;
        public ControllerState State => new(Running, "Ready", "1.22", false, []);
        public event EventHandler? Changed { add { } remove { } }
        public Task ApplyProfileAsync(ControllerProfile profile, CancellationToken token = default) { Applied.Add(profile.Name); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task CheckDriversAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RumbleAsync(int slot, CancellationToken token = default) => Task.CompletedTask;
    }
}
