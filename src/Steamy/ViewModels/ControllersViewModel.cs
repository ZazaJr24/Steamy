using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.ControllerProtocol;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class ControllersViewModel : ObservableObject, IDisposable
{
    private readonly IControllerBackend _backend;
    private readonly ISettingsService _settings;
    private readonly IAppDataStore _store;
    private readonly IWindowsControllers? _windowsControllers;
    private IReadOnlyList<ControllerDevice> _windowsDevices = [];
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private int _refreshQueued;
    private int _nativeRefreshPending;
    private bool _busy;
    private string _status = "", _name = "Default", _curve = "Linear";
    private int _left = 10, _right = 10, _red = 96, _green = 105, _blue = 255;
    private ControllerProfile? _selected;
    private ControllerDevice? _device;
    private Game? _game;
    private string _mappingSource = "Cross";
    private string? _publishedInput, _publishedDeviceInfo;
    private float _lastLX, _lastLY, _lastRX, _lastRY, _lastLT, _lastRT;
    private uint _lastButtons;
    public bool IsTelemetryActive { get; set; }

    public string MappingSource { get => _mappingSource; set { if (Mappings.Any(row => row.Source == value) && SetProperty(ref _mappingSource, value)) OnPropertyChanged(nameof(SelectedMapping)); } }
    public ControllerMappingRow? SelectedMapping => Mappings.FirstOrDefault(row => row.Source == MappingSource);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string BackendStatus => _windowsDevices.Count > 0 ? $"{_windowsDevices.Count} Windows controller{(_windowsDevices.Count == 1 ? "" : "s")} connected · Ready" : _backend.State.Status;
    public string ControllerHostPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ControllerHost", "Steamy.ControllerHost.exe");
    public string DriverStatus => "ViGEmBus: " + _backend.State.DriverVersion + " · HidHide: " + (_backend.State.HidHideInstalled ? "installed" : "optional, not installed");
    private bool ShouldStopBackend => Running || _settings.Load().ControllerBackendEnabled;
    public string ToggleLabel => ShouldStopBackend ? "Stop backend" : "Start backend";
    public bool Running => _backend.State.Running;
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<ControllerDevice> Devices { get; } = [];
    public ObservableCollection<ControllerProfile> Profiles { get; } = [];
    public ObservableCollection<ControllerMappingRow> Mappings { get; } = [];
    public IEnumerable<Game> Games => _store.Games.Where(game => game.InstallState == GameInstallState.Installed && game.AppId > 0).OrderBy(game => game.Name);
    public string[] Curves { get; } = ["Linear", "Enhanced precision", "Quadratic", "Cubic"];
    public string[] Outputs { get; } = ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "Start", "Back", "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Guide", "Unbound"];
    public ControllerProfile? SelectedProfile { get => _selected; set { if (SetProperty(ref _selected, value) && value is not null) Edit(value); } }
    public ControllerDevice? SelectedDevice { get => _device; set { if (SetProperty(ref _device, value)) { OnPropertyChanged(nameof(InputText)); OnPropertyChanged(nameof(DeviceInfo)); OnPropertyChanged(nameof(NativeDeviceVisibility)); OnPropertyChanged(nameof(MapperVisibility)); RumbleCommand?.NotifyCanExecuteChanged(); Refresh(stateChanged: true); } } }
    private ControllerDevice? LiveDevice => _backend.State.Devices.Concat(_windowsDevices).FirstOrDefault(device => device.Id == _device?.Id);
    public Visibility NativeDeviceVisibility => SelectedDevice?.Id.StartsWith("xinput:", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MapperVisibility => NativeDeviceVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public string DeviceInfo => LiveDevice is { } device ? device.Connection + (device.Battery < 0 ? "" : $" · Battery {device.Battery}%") + (device.Charging ? " · Charging" : "") : "No device connected";
    public string InputText => LiveDevice is { } device ? $"L  {device.Input.LX:0.00}, {device.Input.LY:0.00}    R  {device.Input.RX:0.00}, {device.Input.RY:0.00}\nTriggers  {device.Input.LT:P0} / {device.Input.RT:P0}    Buttons  0x{device.Input.Buttons:X4}" : "Connect a DualShock 4 or DualSense with USB or Bluetooth. Xbox controllers can navigate Steamy directly.";
    public Game? SelectedGame { get => _game; set => SetProperty(ref _game, value); }
    public string ProfileName { get => _name; set => SetProperty(ref _name, value); }
    public string Curve { get => _curve; set => SetProperty(ref _curve, value); }
    public int LeftDeadzone { get => _left; set => SetProperty(ref _left, value); }
    public int RightDeadzone { get => _right; set => SetProperty(ref _right, value); }
    public int Red { get => _red; set { if (SetProperty(ref _red, value)) { OnPropertyChanged(nameof(LightbarBrush)); OnPropertyChanged(nameof(LightbarHex)); } } }
    public int Green { get => _green; set { if (SetProperty(ref _green, value)) { OnPropertyChanged(nameof(LightbarBrush)); OnPropertyChanged(nameof(LightbarHex)); } } }
    public int Blue { get => _blue; set { if (SetProperty(ref _blue, value)) { OnPropertyChanged(nameof(LightbarBrush)); OnPropertyChanged(nameof(LightbarHex)); } } }
    public SolidColorBrush LightbarBrush => new(Color.FromRgb((byte)Math.Clamp(Red, 0, 255), (byte)Math.Clamp(Green, 0, 255), (byte)Math.Clamp(Blue, 0, 255)));
    public string LightbarHex => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public bool HasDevice => LiveDevice is not null;
    public Visibility DeviceConnectedVisibility => HasDevice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDeviceVisibility => HasDevice ? Visibility.Collapsed : Visibility.Visible;
    public string DeviceDisplayName => LiveDevice?.Name ?? "No controller detected";
    public string ConnectionBadge => LiveDevice?.Connection ?? "Disconnected";
    public string BatteryText => LiveDevice is { Battery: >= 0 } d ? $"{d.Battery}%" + (d.Charging ? " · Charging" : "") : "N/A";
    public bool IsCharging => LiveDevice?.Charging == true;

    public float LeftStickX => LiveDevice?.Input.LX ?? 0f;
    public float LeftStickY => LiveDevice?.Input.LY ?? 0f;
    public float RightStickX => LiveDevice?.Input.RX ?? 0f;
    public float RightStickY => LiveDevice?.Input.RY ?? 0f;
    public double LeftStickOffsetX => Math.Clamp(LeftStickX * 44.0, -44.0, 44.0);
    public double LeftStickOffsetY => Math.Clamp(-LeftStickY * 44.0, -44.0, 44.0);
    public double RightStickOffsetX => Math.Clamp(RightStickX * 44.0, -44.0, 44.0);
    public double RightStickOffsetY => Math.Clamp(-RightStickY * 44.0, -44.0, 44.0);
    public string LeftStickCoordinatesText => $"X: {LeftStickX:+0.00;-0.00;0.00}   Y: {LeftStickY:+0.00;-0.00;0.00}";
    public string RightStickCoordinatesText => $"X: {RightStickX:+0.00;-0.00;0.00}   Y: {RightStickY:+0.00;-0.00;0.00}";

    public float LeftTrigger => LiveDevice?.Input.LT ?? 0f;
    public float RightTrigger => LiveDevice?.Input.RT ?? 0f;
    public double LeftTriggerPercent => Math.Clamp(LeftTrigger * 100.0, 0.0, 100.0);
    public double RightTriggerPercent => Math.Clamp(RightTrigger * 100.0, 0.0, 100.0);
    public string LeftTriggerText => $"{LeftTriggerPercent:0}%";
    public string RightTriggerText => $"{RightTriggerPercent:0}%";

    public uint Buttons => LiveDevice?.Input.Buttons ?? 0u;
    public bool IsCrossDown => (Buttons & 0x1000) != 0;
    public bool IsCircleDown => (Buttons & 0x2000) != 0;
    public bool IsSquareDown => (Buttons & 0x4000) != 0;
    public bool IsTriangleDown => (Buttons & 0x8000) != 0;
    public bool IsL1Down => (Buttons & 0x0100) != 0;
    public bool IsR1Down => (Buttons & 0x0200) != 0;
    public bool IsL2Down => LeftTrigger > 0.15f;
    public bool IsR2Down => RightTrigger > 0.15f;
    public bool IsL3Down => (Buttons & 0x0040) != 0;
    public bool IsR3Down => (Buttons & 0x0080) != 0;
    public bool IsDpadUpDown => (Buttons & 0x0001) != 0;
    public bool IsDpadDownDown => (Buttons & 0x0002) != 0;
    public bool IsDpadLeftDown => (Buttons & 0x0004) != 0;
    public bool IsDpadRightDown => (Buttons & 0x0008) != 0;
    public bool IsStartDown => (Buttons & 0x0010) != 0;
    public bool IsBackDown => (Buttons & 0x0020) != 0;
    public bool IsGuideDown => (Buttons & 0x0400) != 0;

    public bool NavigationEnabled { get => _settings.Load().ControllerNavigation; set { var options = _settings.Load(); options.ControllerNavigation = value; _ = _settings.SaveAsync(options, _lifetime.Token); if (Application.Current?.MainWindow is MainWindow window) window.RefreshControllerNavigation(); OnPropertyChanged(); } }
    public IAsyncRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand CheckDriversCommand { get; }
    public IAsyncRelayCommand SaveProfileCommand { get; }
    public IRelayCommand NewProfileCommand { get; }
    public IAsyncRelayCommand AssignGameCommand { get; }
    public IAsyncRelayCommand ClearGameCommand { get; }
    public IAsyncRelayCommand RumbleCommand { get; }
    public IRelayCommand OpenDriverPageCommand { get; }
    public IRelayCommand OpenHidHideGuideCommand { get; }
    public IAsyncRelayCommand<string> InstallDriverCommand { get; }
    public IRelayCommand<string> SetColorPresetCommand { get; }

    public ControllersViewModel(IControllerBackend backend, ISettingsService settings, IAppDataStore store, IControllerDrivers? drivers = null, IWindowsControllers? windowsControllers = null)
    {
        _backend = backend; _settings = settings; _store = store; _windowsControllers = windowsControllers;
        foreach (var profile in settings.Load().ControllerProfiles ?? []) Profiles.Add(profile);
        if (Profiles.Count == 0) Profiles.Add(new());
        ToggleCommand = new AsyncRelayCommand(() => RunAsync(token => ShouldStopBackend ? _backend.StopAsync(token) : _backend.StartAsync(token)), () => !_busy);
        CheckDriversCommand = new AsyncRelayCommand(() => RunAsync(_backend.CheckDriversAsync), () => !_busy);
        SaveProfileCommand = new AsyncRelayCommand(() => RunAsync(SaveProfileAsync), () => !_busy);
        NewProfileCommand = new RelayCommand(() => { SelectedProfile = null; Edit(new("New profile")); });
        AssignGameCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            if (SelectedGame is null || SelectedProfile is null) throw new InvalidOperationException("Choose a saved profile and an installed game first.");
            var options = _settings.Load(); options.ControllerGameProfiles ??= []; options.ControllerGameProfiles[SelectedGame.AppId] = SelectedProfile.Name;
            await _settings.SaveAsync(options, token); Status = "Assigned to " + SelectedGame.Name + ". The default profile returns when the game closes.";
        }), () => !_busy);
        ClearGameCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            if (SelectedGame is null) return;
            var options = _settings.Load(); options.ControllerGameProfiles?.Remove(SelectedGame.AppId); await _settings.SaveAsync(options, token); Status = "Game assignment removed.";
        }), () => !_busy);
        RumbleCommand = new AsyncRelayCommand(() => RunAsync(token => SelectedDevice!.Id.StartsWith("xinput:", StringComparison.Ordinal)
            ? _windowsControllers!.RumbleAsync(SelectedDevice.Slot, token) : _backend.RumbleAsync(SelectedDevice.Slot, token)),
            () => !_busy && SelectedDevice is not null && (NativeDeviceVisibility == Visibility.Visible || Running));
        InstallDriverCommand = new AsyncRelayCommand<string>(driver => RunAsync(async token =>
        {
            if (drivers is null || driver is null) return;
            Status = "Downloading and verifying the signed installer. Windows will ask before installation.";
            await drivers.InstallAsync(driver, token); await _backend.CheckDriversAsync(token); Status = "Driver setup finished. Restart Windows if the installer requested it.";
        }), driver => !_busy && drivers is not null && driver is "ViGEmBus" or "HidHide");
        OpenDriverPageCommand = new RelayCommand(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://docs.nefarius.at/projects/ViGEm/End-of-Life/") { UseShellExecute = true }); } catch { Status = "Couldn't open the driver documentation."; } });
        OpenHidHideGuideCommand = new RelayCommand(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/") { UseShellExecute = true }); } catch { Status = "Couldn't open the HidHide guide."; } });
        SetColorPresetCommand = new RelayCommand<string>(hex =>
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7) return;
            if (byte.TryParse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                Red = r; Green = g; Blue = b;
            }
        });
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Name == settings.Load().ControllerDefaultProfile) ?? Profiles[0];
        _backend.Changed += BackendChanged;
        _store.Games.CollectionChanged += GamesChanged;
        RefreshNativeControllers();
    }
    private void Edit(ControllerProfile profile)
    {
        ProfileName = profile.Name; LeftDeadzone = profile.LeftDeadzone; RightDeadzone = profile.RightDeadzone; Curve = profile.Curve; Red = profile.Red; Green = profile.Green; Blue = profile.Blue;
        Mappings.Clear();
        foreach (var (source, output) in new[] { ("Cross", "A"), ("Circle", "B"), ("Square", "X"), ("Triangle", "Y"), ("L1", "LB"), ("R1", "RB"), ("L2", "LT"), ("R2", "RT"), ("L3", "LS"), ("R3", "RS"), ("Options", "Start"), ("Share", "Back"), ("DpadUp", "DpadUp"), ("DpadDown", "DpadDown"), ("DpadLeft", "DpadLeft"), ("DpadRight", "DpadRight"), ("PS", "Guide") })
            Mappings.Add(new(source, profile.Mapping?.GetValueOrDefault(source) ?? output));
        OnPropertyChanged(nameof(SelectedMapping));
        OnPropertyChanged(nameof(LightbarBrush));
        OnPropertyChanged(nameof(LightbarHex));
    }
    private async Task SaveProfileAsync(CancellationToken token)
    {
        var name = ProfileName.Trim();
        if (name.Length is < 1 or > 64 || Profiles.Count >= 50 && !Profiles.Any(profile => profile.Name == name)) throw new InvalidOperationException("Use a profile name of 1–64 characters; at most 50 profiles can be saved.");
        var profile = new ControllerProfile(name, Math.Clamp(LeftDeadzone, 0, 95), Math.Clamp(RightDeadzone, 0, 95), Curve, (byte)Math.Clamp(Red, 0, 255), (byte)Math.Clamp(Green, 0, 255), (byte)Math.Clamp(Blue, 0, 255), Mappings.ToDictionary(row => row.Source, row => row.Output));
        if (Running) await _backend.ApplyProfileAsync(profile, token);
        var old = Profiles.FirstOrDefault(profile => profile.Name == name); if (old is not null) Profiles.Remove(old); Profiles.Add(profile); SelectedProfile = profile;
        var options = _settings.Load(); options.ControllerProfiles = Profiles.ToArray(); options.ControllerDefaultProfile = name; await _settings.SaveAsync(options, token);
        Status = Running ? "Profile saved and applied." : "Profile saved. Start the backend to apply it.";
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return; _busy = true; Status = ""; Refresh();
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status = error is InvalidOperationException or System.IO.IOException ? error.Message : "The controller action couldn't finish. Reconnect and try again."; }
        finally { _busy = false; Refresh(); }
    }
    private void BackendChanged(object? sender, EventArgs args)
    {
        if (_context is not null && SynchronizationContext.Current != _context)
        { if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) _context.Post(_ => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_lifetime.IsCancellationRequested) Refresh(); }, null); }
        else Refresh();
    }
    private void Refresh(bool stateChanged = true)
    {
        var devices = _backend.State.Devices.Concat(_windowsDevices).ToArray(); var id = SelectedDevice?.Id;
        var devicesChanged = !Devices.Select(device => (device.Id, device.Name)).SequenceEqual(devices.Select(device => (device.Id, device.Name)));
        if (devicesChanged) { Devices.Clear(); foreach (var d in devices) Devices.Add(d); }
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == id) ?? Devices.FirstOrDefault();
        var input = InputText;
        if (_publishedInput != input) { _publishedInput = input; OnPropertyChanged(nameof(InputText)); }
        var info = DeviceInfo;
        if (_publishedDeviceInfo != info) { _publishedDeviceInfo = info; OnPropertyChanged(nameof(DeviceInfo)); }

        if (IsTelemetryActive)
        {
            var device = LiveDevice;
            var lx = device?.Input.LX ?? 0f;
            var ly = device?.Input.LY ?? 0f;
            var rx = device?.Input.RX ?? 0f;
            var ry = device?.Input.RY ?? 0f;
            var lt = device?.Input.LT ?? 0f;
            var rt = device?.Input.RT ?? 0f;
            var buttons = device?.Input.Buttons ?? 0u;

            if (_lastLX != lx || _lastLY != ly)
            {
                _lastLX = lx; _lastLY = ly;
                OnPropertyChanged(nameof(LeftStickX));
                OnPropertyChanged(nameof(LeftStickY));
                OnPropertyChanged(nameof(LeftStickOffsetX));
                OnPropertyChanged(nameof(LeftStickOffsetY));
                OnPropertyChanged(nameof(LeftStickCoordinatesText));
            }
            if (_lastRX != rx || _lastRY != ry)
            {
                _lastRX = rx; _lastRY = ry;
                OnPropertyChanged(nameof(RightStickX));
                OnPropertyChanged(nameof(RightStickY));
                OnPropertyChanged(nameof(RightStickOffsetX));
                OnPropertyChanged(nameof(RightStickOffsetY));
                OnPropertyChanged(nameof(RightStickCoordinatesText));
            }
            if (_lastLT != lt)
            {
                _lastLT = lt;
                OnPropertyChanged(nameof(LeftTrigger));
                OnPropertyChanged(nameof(LeftTriggerPercent));
                OnPropertyChanged(nameof(LeftTriggerText));
                OnPropertyChanged(nameof(IsL2Down));
            }
            if (_lastRT != rt)
            {
                _lastRT = rt;
                OnPropertyChanged(nameof(RightTrigger));
                OnPropertyChanged(nameof(RightTriggerPercent));
                OnPropertyChanged(nameof(RightTriggerText));
                OnPropertyChanged(nameof(IsR2Down));
            }
            if (_lastButtons != buttons)
            {
                _lastButtons = buttons;
                OnPropertyChanged(nameof(Buttons));
                OnPropertyChanged(nameof(IsCrossDown));
                OnPropertyChanged(nameof(IsCircleDown));
                OnPropertyChanged(nameof(IsSquareDown));
                OnPropertyChanged(nameof(IsTriangleDown));
                OnPropertyChanged(nameof(IsL1Down));
                OnPropertyChanged(nameof(IsR1Down));
                OnPropertyChanged(nameof(IsL3Down));
                OnPropertyChanged(nameof(IsR3Down));
                OnPropertyChanged(nameof(IsDpadUpDown));
                OnPropertyChanged(nameof(IsDpadDownDown));
                OnPropertyChanged(nameof(IsDpadLeftDown));
                OnPropertyChanged(nameof(IsDpadRightDown));
                OnPropertyChanged(nameof(IsStartDown));
                OnPropertyChanged(nameof(IsBackDown));
                OnPropertyChanged(nameof(IsGuideDown));
            }
        }

        if (devicesChanged || stateChanged)
        {
            OnPropertyChanged(nameof(HasDevice));
            OnPropertyChanged(nameof(DeviceConnectedVisibility));
            OnPropertyChanged(nameof(NoDeviceVisibility));
            OnPropertyChanged(nameof(DeviceDisplayName));
            OnPropertyChanged(nameof(ConnectionBadge));
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(IsCharging));
        }

        // Native input polling must not invalidate the entire settings form and
        // every command ten times per second when only a stick value changed.
        if (!stateChanged && !devicesChanged) return;
        foreach (var name in new[] { nameof(BackendStatus), nameof(DriverStatus), nameof(ToggleLabel), nameof(Running), nameof(BusyVisibility) }) OnPropertyChanged(name);
        foreach (var command in new IRelayCommand[] { ToggleCommand, CheckDriversCommand, SaveProfileCommand, AssignGameCommand, ClearGameCommand, RumbleCommand, InstallDriverCommand }) command?.NotifyCanExecuteChanged();
    }
    internal void RefreshNativeControllers()
    { if (_lifetime.IsCancellationRequested) return; _windowsDevices = _windowsControllers?.Read() ?? []; Refresh(stateChanged: false); }
    internal async Task RefreshNativeControllersAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _nativeRefreshPending, 1) != 0) return;
        try
        {
            var devices = await Task.Run(() => _windowsControllers?.Read() ?? [], token);
            if (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested) { _windowsDevices = devices; Refresh(stateChanged: false); }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _nativeRefreshPending, 0); }
    }
    private void GamesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => OnPropertyChanged(nameof(Games));
    public void Dispose() { _lifetime.Cancel(); _backend.Changed -= BackendChanged; _store.Games.CollectionChanged -= GamesChanged; }
}
public sealed class ControllerMappingRow(string source, string output) : ObservableObject
{
    private string _output = output;
    public string Source { get; } = source;
    public string Output { get => _output; set => SetProperty(ref _output, value); }
}
