namespace Steamy.Services;

public interface IControllerGameProfiles
{
    Task PrepareLaunchAsync(int appId, CancellationToken token = default);
    Task RestoreDefaultAsync(CancellationToken token = default);
}
public sealed class ControllerGameProfiles(IControllerBackend backend, ISettingsService settings, IPlayTimeService playtime, TimeProvider? time = null) : IControllerGameProfiles, IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1);
    private Task? _monitor;
    private int _launching;
    private DateTimeOffset _launchDeadline;
    private string _applied = "";
    public void Start() => _monitor ??= MonitorAsync();
    public async Task PrepareLaunchAsync(int appId, CancellationToken token = default)
    {
        if (!backend.State.Running) return;
        _launching = appId; _launchDeadline = _time.GetUtcNow().AddSeconds(120);
        await ApplyAsync(appId, token); Start();
    }
    public async Task RestoreDefaultAsync(CancellationToken token = default)
    { _launching = 0; await ApplyAsync(0, token); }
    private async Task ApplyAsync(int appId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!backend.State.Running) { _applied = ""; return; }
            var options = settings.Load();
            var name = appId > 0 && options.ControllerGameProfiles?.TryGetValue(appId, out var assignment) == true ? assignment : options.ControllerDefaultProfile;
            var profile = options.ControllerProfiles?.FirstOrDefault(profile => profile.Name == name) ?? options.ControllerProfiles?.FirstOrDefault(profile => profile.Name == options.ControllerDefaultProfile) ?? new ControllerProtocol.ControllerProfile();
            if (_applied == profile.Name) return;
            await backend.ApplyProfileAsync(profile, token); _applied = profile.Name;
        }
        finally { _gate.Release(); }
    }
    private async Task MonitorAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _lifetime.Token);
                await RefreshAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) { if (_lifetime.IsCancellationRequested) break; }
            catch { /* The controller page retains driver/reconnect failures; retry on the next fresh snapshot. */ }
        }
    }
    internal async Task RefreshAsync(CancellationToken token = default)
    {
        if (!backend.State.Running) { _applied = ""; return; }
        var options = settings.Load();
        if (options.ControllerGameProfiles is not { Count: > 0 } && _applied.Length == 0) return;
        var snapshot = await playtime.ReadAsync(token);
        var fresh = _time.GetUtcNow() - snapshot.UpdatedAt < TimeSpan.FromSeconds(20);
        var running = fresh ? snapshot.Games.FirstOrDefault(game => game.IsRunning && options.ControllerGameProfiles?.ContainsKey(game.AppId) == true)?.AppId ?? 0 : 0;
        if (_launching != 0)
        {
            if (fresh && snapshot.Games.Any(game => game.AppId == _launching && game.IsRunning)) _launching = 0;
            else if (_time.GetUtcNow() < _launchDeadline) running = _launching;
            else _launching = 0;
        }
        await ApplyAsync(running, token);
    }
    public void Dispose() => _lifetime.Cancel();
}
