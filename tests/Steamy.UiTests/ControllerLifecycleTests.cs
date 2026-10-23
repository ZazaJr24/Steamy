using Steamy.ControllerProtocol;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class ControllerLifecycleTests
{
    [Fact]
    public async Task WindowsControllersAppearWithoutStartingTheSonyBackendAndHotplugKeepsSelection()
    {
        var commands = new List<string>(); var settings = new Settings();
        using var backend = new ControllerBackendService(settings, (request, _) => { commands.Add(request.Method); return Task.FromResult(Reply(request, false)); });
        var native = new WindowsFixture();
        native.Devices = [Pad(9, "xinput:1")];
        using var model = new ControllersViewModel(backend, settings, new AppDataStore(), windowsControllers: native);
        Assert.Equal("xinput:1", Assert.Single(model.Devices).Id); Assert.False(model.Running); Assert.Empty(commands);
        Assert.Equal(System.Windows.Visibility.Visible, model.NativeDeviceVisibility);
        Assert.True(model.RumbleCommand.CanExecute(null)); await model.RumbleCommand.ExecuteAsync(null); Assert.Equal(9, native.Rumbled);
        native.Devices = [Pad(8, "xinput:0"), Pad(9, "xinput:1") with { Input = new(.5f, 0, 0, 0, 0, 1, 0x1000) }];
        model.RefreshNativeControllers(); Assert.Equal("xinput:1", model.SelectedDevice!.Id); Assert.Contains("0.50", model.InputText.Replace(',', '.'));
        native.Devices = [Pad(8, "xinput:0")]; model.RefreshNativeControllers(); Assert.Equal("xinput:0", model.SelectedDevice!.Id);
        native.Devices = []; model.RefreshNativeControllers(); Assert.Empty(model.Devices); Assert.Null(model.SelectedDevice); Assert.False(model.RumbleCommand.CanExecute(null)); Assert.Empty(commands);
    }
    private static ControllerDevice Pad(int slot, string id) => new(slot, id, "Windows controller", "XInput", -1, false, new(0, 0, 0, 0, 0, 0, 0));
    [Fact]
    public void StableNativePollingDoesNotRefreshTheFormButInputChangesStillPublish()
    {
        var native = new WindowsFixture { Devices = [Pad(8, "xinput:0")] };
        using var backend = new ControllerBackendService(new Settings(), (request, _) => Task.FromResult(Reply(request, false)));
        using var model = new ControllersViewModel(backend, new Settings(), new AppDataStore(), windowsControllers: native);
        var changes = new List<string?>(); var commandChanges = 0;
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        model.RumbleCommand.CanExecuteChanged += (_, _) => commandChanges++;
        for (var i = 0; i < 100; i++) model.RefreshNativeControllers();
        Assert.Empty(changes); Assert.Equal(0, commandChanges);
        native.Devices = [Pad(8, "xinput:0") with { Input = new(.5f, 0, 0, 0, 0, 0, 0) }];
        model.RefreshNativeControllers();
        Assert.Equal(new[] { nameof(ControllersViewModel.InputText) }, changes);
        Assert.Equal(0, commandChanges);
    }
    private sealed class WindowsFixture : IWindowsControllers
    {
        public IReadOnlyList<ControllerDevice> Devices = []; public int Rumbled = -1;
        public IReadOnlyList<ControllerDevice> Read() => Devices;
        public Task RumbleAsync(int slot, CancellationToken token) { Rumbled = slot; return Task.CompletedTask; }
    }
    [Fact]
    public async Task StopCancelsDelayedRecoveryAndKeepsTheSavedBackendDisabled()
    {
        var settings = new Settings(); var commands = new List<string>(); var running = false;
        using var backend = new ControllerBackendService(settings, (request, _) =>
        {
            commands.Add(request.Method);
            if (request.Method == "start") running = true;
            if (request.Method == "stop") running = false;
            return Task.FromResult(Reply(request, running));
        });
        await backend.StartAsync();
        var recovery = backend.RecoverAfterAsync(TimeSpan.FromMilliseconds(60));
        await backend.StopAsync(); await recovery;
        Assert.Equal(1, commands.Count(command => command == "start"));
        Assert.Equal("stop", commands.Last()); Assert.False(settings.Load().ControllerBackendEnabled); Assert.False(backend.State.Running);
    }

    [Fact]
    public async Task StopWaitsForAnInflightStartAndWinsTheFinalState()
    {
        var settings = new Settings(); var commands = new List<string>();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var backend = new ControllerBackendService(settings, async (request, token) =>
        {
            commands.Add(request.Method);
            if (request.Method == "start") { starting.TrySetResult(); await finishStart.Task.WaitAsync(token); }
            return Reply(request, request.Method == "start");
        });
        var start = backend.StartAsync(); await starting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = backend.StopAsync(); finishStart.SetResult(); await Task.WhenAll(start, stop);
        Assert.Equal("stop", commands.Last()); Assert.False(settings.Load().ControllerBackendEnabled); Assert.False(backend.State.Running);
    }

    [Fact]
    public async Task ANewExplicitStartSupersedesTheOldRecovery()
    {
        var settings = new Settings(); var starts = 0;
        using var backend = new ControllerBackendService(settings, (request, _) =>
        {
            if (request.Method == "start") starts++;
            return Task.FromResult(Reply(request, request.Method == "start"));
        });
        await backend.StartAsync(); var recovery = backend.RecoverAfterAsync(TimeSpan.FromMilliseconds(60));
        await backend.StopAsync(); await backend.StartAsync(); await recovery;
        Assert.Equal(2, starts); Assert.True(settings.Load().ControllerBackendEnabled); Assert.True(backend.State.Running);
    }

    private static ControllerResponse Reply(ControllerRequest request, bool running) => new(1, request.Id, true, "", new(running, running ? "Ready" : "Stopped", "fixture", false, []));
    [Fact]
    public async Task TheUiCanDisableAnEnabledBackendWhileItIsDisconnected()
    {
        var settings = new Settings(); var commands = new List<string>();
        using var backend = new ControllerBackendService(settings, (request, _) =>
        {
            commands.Add(request.Method);
            return Task.FromResult(Reply(request, request.Method == "start"));
        });
        await backend.StartAsync(); await backend.CheckDriversAsync();
        Assert.True(settings.Load().ControllerBackendEnabled); Assert.False(backend.State.Running);
        using var model = new ControllersViewModel(backend, settings, new AppDataStore());
        Assert.Equal("Stop backend", model.ToggleLabel);
        await model.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("stop", commands.Last()); Assert.False(settings.Load().ControllerBackendEnabled);
        Assert.Equal("Start backend", model.ToggleLabel);
    }
    private sealed class Settings : ISettingsService
    {
        private AppSettings _options = new();
        public AppSettings Load() => _options;
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) { _options = settings; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
