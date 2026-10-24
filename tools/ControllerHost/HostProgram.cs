// SPDX-License-Identifier: GPL-3.0-or-later
// Steamy's headless entry point for the separately distributed DS4Windows fork.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DS4Windows;
using Steamy.ControllerProtocol;

namespace Steamy.ControllerHost;

internal static class HostProgram
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static readonly ControllerProfile[] Profiles = Enumerable.Range(0, 8).Select(_ => new ControllerProfile()).ToArray();
    private static ControlService Hub;
    private static volatile bool Running;
    private static int RunGeneration;
    private static readonly object EngineGate = new();
    private static string Status = "Stopped";
    private static Task HotplugTask = Task.CompletedTask;
    private static readonly Dictionary<int, string> DeviceIds = new();

    [STAThread]
    public static int Main(string[] args)
    {
        var secret = Environment.GetEnvironmentVariable("STEAMY_CONTROLLER_SECRET");
        var pipe = Environment.GetEnvironmentVariable("STEAMY_CONTROLLER_PIPE");
        if (secret == null || secret.Length != 64 || !secret.All(Uri.IsHexDigit) || pipe == null || !pipe.StartsWith("Steamy.Controller.v1.", StringComparison.Ordinal)
            || pipe.Length > 100 || pipe.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '.'))
            || !int.TryParse(Environment.GetEnvironmentVariable("STEAMY_CONTROLLER_PARENT"), out var parentId)) return 2;
        Environment.SetEnvironmentVariable("STEAMY_CONTROLLER_SECRET", null);
        using var parent = Process.GetProcessById(parentId);
        parent.EnableRaisingEvents = true; parent.Exited += (_, _) => Lifetime.Cancel();
        if (parent.HasExited) return 2;
        var testData = Environment.GetEnvironmentVariable("STEAMY_CONTROLLER_TEST_DATA");
        var mutexName = string.IsNullOrEmpty(testData)
            ? "Local\\Steamy.ControllerHost." + Environment.UserName
            : "Local\\Steamy.ControllerHost.Test." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(testData)));
        using var ownedSession = new Mutex(true, mutexName, out var ownsSession);
        if (!ownsSession) return 3;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = Task.Run(async () =>
        {
            try { await RunPipeAsync(pipe, secret, Lifetime.Token); }
            catch (Exception) { /* Details stay in the parent UI; never write device IDs or handshake data. */ }
            finally
            {
                Lifetime.Cancel(); Stop();
                _ = app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
            }
        });
        app.Run(); return 0;
    }

    private static async Task RunPipeAsync(string name, string secret, CancellationToken token)
    {
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
        var hello = await ControllerWire.ReadAsync<ControllerRequest>(server, token).WaitAsync(TimeSpan.FromSeconds(5), token);
        if (hello.Version != ControllerWire.Version || hello.Method != "hello" || hello.Secret == null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(hello.Secret))) return;
        await ControllerWire.WriteAsync(server, new ControllerResponse(1, hello.Id, true, "", Snapshot()), token);
        while (!token.IsCancellationRequested && server.IsConnected)
        {
            var request = await ControllerWire.ReadAsync<ControllerRequest>(server, token);
            ControllerResponse response;
            try
            {
                if (request.Version != 1 || request.Secret != null) throw new InvalidDataException("Unsupported controller protocol.");
                switch (request.Method)
                {
                    case "state": break;
                    case "drivers": Initialize(); Global.RefreshViGEmBusInfo(); Global.RefreshHidHideInfo(); break;
                    case "start": Start(); break;
                    case "stop": Stop(); break;
                    case "profile": await ApplyProfileAsync(request.Slot, request.Profile, token); break;
                    case "rumble": await RumbleAsync(request.Slot, request.Heavy, request.Light, token); break;
                    default: throw new InvalidDataException("Unsupported controller command.");
                }
                response = new(1, request.Id, true, "", Snapshot());
            }
            catch (Exception error)
            {
                var message = error is ArgumentException or InvalidOperationException or InvalidDataException ? error.Message : "The controller backend couldn't complete this action. Reconnect your controller or restart the backend.";
                response = new(1, request.Id, false, message, Snapshot());
            }
            await ControllerWire.WriteAsync(server, response, token);
        }
    }

    private static void Initialize()
    {
        if (Hub != null) return;
        var path = Environment.GetEnvironmentVariable("STEAMY_CONTROLLER_TEST_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "Controllers", "Backend");
        Directory.CreateDirectory(Path.Combine(path, "Profiles")); Directory.CreateDirectory(Path.Combine(path, "Logs"));
        Global.SaveWhere(path); Global.RefreshViGEmBusInfo(); Global.RefreshHidHideInfo();
        // No settings, actions, output servers or auto-profiles from another DS4Windows installation.
        Global.UseExclusiveMode = false;
        Global.DeviceOptions.DualSenseOpts.Enabled = true;
        Global.DeviceOptions.SwitchProDeviceOpts.Enabled = false;
        Global.DeviceOptions.JoyConDeviceOpts.Enabled = false;
        Hub = new ControlService(new DS4WinWPF.ArgumentParser());
        DS4WinWPF.App.rootHub = Hub; DS4Windows.Program.rootHub = Hub;
        DS4WinWPF.App.requestClient = new HttpClient();
        for (var slot = 0; slot < Profiles.Length; slot++)
        {
            Global.ProfilePath[slot] = "SteamyDefault";
            Global.LoadBlankDevProfile(slot, false, Hub, false, false);
            Global.DinputOnly[slot] = false;
        }
        if (!File.Exists(Path.Combine(path, "Profiles", "SteamyDefault.xml"))) Global.SaveProfile(0, "SteamyDefault");
        Hub.HotplugController += (_, device, slot) => device.queueEvent(() => ApplyProfile(slot, Profiles[slot]));
    }
    private static void Start()
    {
        lock (EngineGate)
        {
        if (Running) return;
        if (Process.GetProcessesByName("DS4Windows").Any(process => { process.Dispose(); return true; })) throw new InvalidOperationException("Close DS4Windows before starting Steamy's backend to avoid duplicate input.");
        Initialize(); Global.RefreshViGEmBusInfo(); Global.RefreshHidHideInfo();
        if (!Global.IsViGEmBusInstalled()) { Status = "ViGEmBus is missing. Open driver setup in Steamy."; throw new InvalidOperationException(Status); }
        Running = Hub.Start(false);
        Status = Running ? "Ready" : "The driver couldn't be opened. Check driver setup.";
        if (!Running) throw new InvalidOperationException(Status);
        for (var slot = 0; slot < Hub.DS4Controllers.Length; slot++) if (Hub.DS4Controllers[slot] != null) { var captured = slot; Hub.DS4Controllers[slot].queueEvent(() => ApplyProfile(captured, Profiles[captured])); }
        var generation = ++RunGeneration;
        HotplugTask = Task.Run(async () =>
        {
            while (Running && generation == RunGeneration && !Lifetime.IsCancellationRequested)
            { try { await Task.Delay(1000, Lifetime.Token); lock (EngineGate) { if (Running && generation == RunGeneration) Hub.HotPlug(); } } catch (OperationCanceledException) { break; } catch { Status = "A controller reconnect is pending."; } }
        });
        }
    }
    private static void Stop()
    {
        lock (EngineGate)
        {
        RunGeneration++;
        var wasRunning = Running; Running = false;
        try { if (wasRunning && Hub != null) Hub.Stop(false, true); } catch { }
        Status = "Stopped";
        }
    }
    private static ControllerState Snapshot()
    {
        var devices = new List<ControllerDevice>();
        if (Running && Hub != null)
        {
            for (var slot = 0; slot < Hub.DS4Controllers.Length; slot++)
            {
                var device = Hub.DS4Controllers[slot]; if (device == null || !device.IsAlive()) continue;
                var state = new DS4State(); device.getCurrentStateRef().CopyTo(state);
                if (!DeviceIds.TryGetValue(slot, out var id)) DeviceIds[slot] = id = Guid.NewGuid().ToString("N");
                uint buttons = 0;
                var flags = new[] { state.DpadUp, state.DpadDown, state.DpadLeft, state.DpadRight, state.Options, state.Share, state.L3, state.R3, state.L1, state.R1, false, false, state.Cross, state.Circle, state.Square, state.Triangle };
                for (var bit = 0; bit < flags.Length; bit++) if (flags[bit]) buttons |= 1u << bit;
                devices.Add(new(slot, id, device.DisplayName, device.ConnectionType.ToString(), device.Battery, device.Charging,
                    new((state.LX - 127.5f) / 127.5f, (127.5f - state.LY) / 127.5f, (state.RX - 127.5f) / 127.5f, (127.5f - state.RY) / 127.5f, state.L2 / 255f, state.R2 / 255f, buttons)));
            }
        }
        return new(Running, Status, Hub == null ? "Not checked" : Global.vigembusVersion, Hub != null && Global.hidHideInstalled, devices);
    }
    private static async Task ApplyProfileAsync(int slot, ControllerProfile profile, CancellationToken token)
    {
        if (slot is < 0 or >= 8 || profile == null || profile.Name is null or { Length: > 64 } || profile.LeftDeadzone is < 0 or > 95 || profile.RightDeadzone is < 0 or > 95
            || profile.Curve is not ("Linear" or "Enhanced precision" or "Quadratic" or "Cubic") || profile.Mapping?.Count > 18) throw new ArgumentException("Invalid controller profile.");
        if (profile.Mapping != null) foreach (var pair in profile.Mapping)
        {
            if (!InputControls.Contains(pair.Key) || !OutputControls.Contains(pair.Value)) throw new ArgumentException("Choose a supported gamepad mapping.");
        }
        Profiles[slot] = profile;
        if (!Running || Hub.DS4Controllers[slot] is not { } device) return;
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.queueEvent(() => { try { ApplyProfile(slot, profile); applied.TrySetResult(); } catch (Exception error) { applied.TrySetException(error); } });
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
    }
    private static readonly string[] InputControls = { "Cross", "Circle", "Square", "Triangle", "L1", "R1", "L2", "R2", "L3", "R3", "Options", "Share", "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "PS", "Touchpad" };
    private static readonly string[] OutputControls = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "Start", "Back", "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Guide", "Unbound" };
    private static void ApplyProfile(int slot, ControllerProfile profile)
    {
        Global.GetLSDeadInfo(slot).deadZone = (int)Math.Round(profile.LeftDeadzone * 127d / 100);
        Global.GetRSDeadInfo(slot).deadZone = (int)Math.Round(profile.RightDeadzone * 127d / 100);
        Global.GetLSDeadInfo(slot).antiDeadZone = Global.GetRSDeadInfo(slot).antiDeadZone = 0;
        var curve = Array.IndexOf(new[] { "Linear", "Enhanced precision", "Quadratic", "Cubic" }, profile.Curve);
        Global.setLsOutCurveMode(slot, curve); Global.setRsOutCurveMode(slot, curve);
        var led = Global.getLightbarSettingsInfo(slot).ds4winSettings;
        led.useCustomLed = true; led.ledAsBattery = false; led.m_CustomLed = new(profile.Red, profile.Green, profile.Blue);
        foreach (var setting in Global.getDS4CSettings(slot)) setting.Reset();
        if (profile.Mapping != null) foreach (var pair in profile.Mapping) Global.UpdateDS4CSetting(slot, pair.Key, false, Enum.Parse<X360Controls>(pair.Value), "", DS4KeyType.None);
        Global.CacheExtraProfileInfo(slot);
    }
    private static async Task RumbleAsync(int slot, byte heavy, byte light, CancellationToken token)
    {
        if (!Running || slot is < 0 or >= 8 || Hub.DS4Controllers[slot] == null) throw new InvalidOperationException("Connect a controller before testing vibration.");
        Hub.setRumble(heavy, light, slot);
        try { await Task.Delay(400, token); } finally { Hub.setRumble(0, 0, slot); }
    }
}
