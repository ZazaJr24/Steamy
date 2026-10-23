using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using Steamy.ControllerProtocol;

namespace Steamy.Services;

public interface IControllerBackend
{
    bool IsInstalled { get; }
    ControllerState State { get; }
    event EventHandler? Changed;
    Task StartAsync(CancellationToken token = default);
    Task StopAsync(CancellationToken token = default);
    Task CheckDriversAsync(CancellationToken token = default);
    Task ApplyProfileAsync(ControllerProfile profile, CancellationToken token = default);
    Task RumbleAsync(int slot, CancellationToken token = default);
}
public sealed class ControllerBackendService(ISettingsService settings) : IControllerBackend, IDisposable
{
    private readonly SemaphoreSlim _wireGate = new(1);
    private readonly SemaphoreSlim _controlGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ControllerRequest, CancellationToken, Task<ControllerResponse>>? _exchange;
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private long _requestId;
    private Task? _poll;
    private volatile bool _enabled;
    private bool _disposed;
    private int _intentRevision;
    private ControllerState _state = new(false, "Stopped", "Not checked", false, []);
    public ControllerState State => Volatile.Read(ref _state);
    public bool IsInstalled => File.Exists(Executable);
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "Tools", "ControllerHost", "Steamy.ControllerHost.exe");
    public event EventHandler? Changed;
    internal ControllerBackendService(ISettingsService settings, Func<ControllerRequest, CancellationToken, Task<ControllerResponse>> exchange) : this(settings) => _exchange = exchange;
    private void Publish(ControllerState state)
    {
        var old = State; Volatile.Write(ref _state, state);
        if (old.Running != state.Running || old.Status != state.Status || old.DriverVersion != state.DriverVersion || old.HidHideInstalled != state.HidHideInstalled || !old.Devices.SequenceEqual(state.Devices)) Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task StartAsync(CancellationToken token = default)
    {
        var wasEnabled = _enabled;
        var revision = Interlocked.Increment(ref _intentRevision);
        _enabled = true;
        var acquired = false;
        try
        {
            await _controlGate.WaitAsync(token); acquired = true;
            if (!_enabled || revision != Volatile.Read(ref _intentRevision)) return;
            await StartCoreAsync(token);
            if (_enabled && revision == Volatile.Read(ref _intentRevision))
            {
                var options = settings.Load(); options.ControllerBackendEnabled = true; await settings.SaveAsync(options, token);
            }
        }
        catch { if (revision == Volatile.Read(ref _intentRevision)) _enabled = wasEnabled; throw; }
        finally { if (acquired) _controlGate.Release(); }
    }
    private async Task StartCoreAsync(CancellationToken token)
    {
        var options = settings.Load();
        var profile = options.ControllerProfiles?.FirstOrDefault(profile => profile.Name == options.ControllerDefaultProfile) ?? new();
        await ApplyProfileAsync(profile, token);
        await RequestAsync("start", token);
    }
    public async Task StopAsync(CancellationToken token = default)
    {
        _enabled = false;
        Interlocked.Increment(ref _intentRevision);
        await _controlGate.WaitAsync(token);
        try
        {
            // Save first: a stopped/crashed helper must not return on the next launch.
            var options = settings.Load(); options.ControllerBackendEnabled = false; await settings.SaveAsync(options, token);
            if (_pipe?.IsConnected == true || _exchange is not null) await RequestAsync("stop", token);
            else Publish(State with { Running = false, Status = "Stopped", Devices = [] });
        }
        finally { _controlGate.Release(); }
    }
    public Task CheckDriversAsync(CancellationToken token = default) => RequestAsync("drivers", token);
    public async Task ApplyProfileAsync(ControllerProfile profile, CancellationToken token = default)
    {
        for (var slot = 0; slot < 8; slot++) await RequestAsync("profile", token, slot, profile);
    }
    public Task RumbleAsync(int slot, CancellationToken token = default) => RequestAsync("rumble", token, slot, heavy: 100, light: 70);
    private async Task RequestAsync(string method, CancellationToken token, int slot = 0, ControllerProfile? profile = null, byte heavy = 0, byte light = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await _wireGate.WaitAsync(deadline.Token);
        try
        {
            if (_exchange is null) await EnsureConnectedAsync(deadline.Token);
            var id = Interlocked.Increment(ref _requestId);
            var request = new ControllerRequest(1, id, method, Slot: slot, Profile: profile, Heavy: heavy, Light: light);
            ControllerResponse response;
            if (_exchange is not null) response = await _exchange(request, deadline.Token);
            else
            {
                await ControllerWire.WriteAsync(_pipe!, request, deadline.Token);
                response = await ControllerWire.ReadAsync<ControllerResponse>(_pipe!, deadline.Token);
            }
            Validate(response, id);
            if (response.State is { } state) Publish(state);
            if (!response.Ok) throw new InvalidOperationException(response.Error);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            CloseConnection(); Publish(new(false, "Controller backend disconnected. Reconnect or restart it here.", State.DriverVersion, State.HidHideInstalled, []));
            if (token.IsCancellationRequested || _lifetime.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new IOException("The controller backend didn't respond. Restart it and reconnect your controller.", error);
        }
        finally { _wireGate.Release(); }
    }
    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_pipe?.IsConnected == true) return;
        if (!IsInstalled) throw new FileNotFoundException("The controller backend is missing. Use the complete Steamy package.");
        CloseConnection();
        var name = "Steamy.Controller.v1." + Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var request = new ProcessStartInfo(Executable)
        { WorkingDirectory = Path.GetDirectoryName(Executable)!, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        request.Environment["STEAMY_CONTROLLER_PIPE"] = name; request.Environment["STEAMY_CONTROLLER_SECRET"] = secret;
        request.Environment["STEAMY_CONTROLLER_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _process = Process.Start(request) ?? throw new IOException("The controller backend could not start.");
        _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await _pipe.ConnectAsync(8000, token);
        var id = Interlocked.Increment(ref _requestId);
        await ControllerWire.WriteAsync(_pipe, new ControllerRequest(1, id, "hello", secret), token);
        var hello = await ControllerWire.ReadAsync<ControllerResponse>(_pipe, token); Validate(hello, id);
        if (!hello.Ok) throw new IOException("The controller backend rejected the local session.");
        if (_poll is null) _poll = Task.Run(PollAsync);
    }
    internal static void Validate(ControllerResponse response, long id)
    {
        if (response.Version != 1 || response.Id != id || response.Error is null or { Length: > 1000 } || response.State is { } state &&
            (state.Status is null or { Length: > 1000 } || state.DriverVersion is null or { Length: > 100 } || state.Devices is null || state.Devices.Count > 8 || state.Devices.Any(device => device is null)
                || state.Devices.Select(device => device.Slot).Distinct().Count() != state.Devices.Count
                || state.Devices.Any(device => device is null || device.Slot is < 0 or > 7 || device.Name is null or { Length: > 240 } || device.Id is null or { Length: > 100 } || device.Connection is null or { Length: > 40 }
                || device.Battery is < 0 or > 100 || device.Input is null || !ValidAxis(device.Input.LX) || !ValidAxis(device.Input.LY) || !ValidAxis(device.Input.RX) || !ValidAxis(device.Input.RY)
                || !float.IsFinite(device.Input.LT) || !float.IsFinite(device.Input.RT) || device.Input.LT is < 0 or > 1 || device.Input.RT is < 0 or > 1)))
            throw new InvalidDataException("Invalid controller backend response.");
    }
    private static bool ValidAxis(float axis) => float.IsFinite(axis) && axis is >= -1.01f and <= 1.01f;
    private async Task PollAsync()
    {
        var attempts = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(State.Running && State.Devices.Count > 0 ? 50 : 1000, _lifetime.Token);
                if (_pipe?.IsConnected == true) { await RequestAsync("state", _lifetime.Token); attempts = 0; }
                else if (_enabled && attempts < 3) { attempts++; await RecoverAfterAsync(TimeSpan.FromSeconds(attempts), _lifetime.Token); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch { /* Bounded recovery; the visible status retains the failure. */ }
        }
    }
    internal async Task RecoverAfterAsync(TimeSpan delay, CancellationToken token = default)
    {
        var revision = Volatile.Read(ref _intentRevision);
        if (!_enabled) return;
        await Task.Delay(delay, token);
        await _controlGate.WaitAsync(token);
        try
        {
            // Stop or a newer explicit Start supersedes a delayed recovery. Recovery
            // never changes the saved enabled preference or creates a new intent.
            if (!_disposed && _enabled && revision == Volatile.Read(ref _intentRevision)) await StartCoreAsync(token);
        }
        finally { _controlGate.Release(); }
    }
    private void CloseConnection(bool graceful = false)
    {
        _pipe?.Dispose(); _pipe = null;
        if (_process is { } process)
        {
            _process = null;
            if (graceful)
            {
                _ = Task.Run(async () => { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { try { if (!process.HasExited) process.Kill(); } catch { } } finally { process.Dispose(); } });
                return;
            }
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _enabled = false; _lifetime.Cancel();
        Interlocked.Increment(ref _intentRevision);
        // The pipe closes first; the owned helper also monitors its parent process.
        CloseConnection(graceful: true);
    }
}
