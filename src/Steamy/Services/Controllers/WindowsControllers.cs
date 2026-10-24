using System.Runtime.InteropServices;
using System.IO;
using Steamy.ControllerProtocol;

namespace Steamy.Services;

public interface IWindowsControllers
{
    IReadOnlyList<ControllerDevice> Read();
    Task RumbleAsync(int slot, CancellationToken token);
}

// XInput is independent of the optional Sony-to-Xbox mapper. A controller
// exposed by Steam Input or another mapper is usable through the same API.
public sealed class WindowsControllers : IWindowsControllers
{
    private readonly object _gate = new();
    private readonly long[] _retryAt = new long[4], _batteryAt = new long[4];
    private readonly uint[] _packets = new uint[4];
    private readonly ControllerDevice?[] _cached = new ControllerDevice?[4];
    private readonly Func<int, (uint Packet, ControllerInput Input)?> _readState;
    private readonly Func<int, (string Connection, int Percent)> _readBattery;
    private readonly Func<long> _clock;
    public WindowsControllers() : this(ReadState, ReadBattery, () => Environment.TickCount64) { }
    internal WindowsControllers(Func<int, (uint Packet, ControllerInput Input)?> readState,
        Func<int, (string Connection, int Percent)> readBattery, Func<long> clock)
    { _readState = readState; _readBattery = readBattery; _clock = clock; }
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct Battery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)] private struct Vibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte device, out Battery battery);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref Vibration vibration);

    public IReadOnlyList<ControllerDevice> Read()
    {
        lock (_gate) return ReadCore();
    }
    private IReadOnlyList<ControllerDevice> ReadCore()
    {
        var devices = new List<ControllerDevice>();
        var now = _clock();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                // Disconnected XInput slots can be expensive to query. Hotplug detection
                // stays at one second while connected pads retain the live input cadence.
                if (now < _retryAt[index]) continue;
                if (_readState(index) is not { } state)
                {
                    _cached[index] = null; _retryAt[index] = now + 1000; _batteryAt[index] = 0;
                    continue;
                }
                var cached = _cached[index];
                var (connection, batteryPercent) = cached is null ? ("XInput · Windows controller", -1) : (cached.Connection, cached.Battery);
                if (cached is null || now >= _batteryAt[index])
                { (connection, batteryPercent) = _readBattery(index); _batteryAt[index] = now + 5000; }
                // Keep local XInput slots separate from the mapper's 0–7 slots.
                if (cached is null || state.Packet != _packets[index] || cached.Connection != connection || cached.Battery != batteryPercent)
                    cached = new(8 + index, "xinput:" + index, $"XInput Controller #{index + 1}", connection, batteryPercent, false, state.Input);
                _packets[index] = state.Packet; _cached[index] = cached;
                devices.Add(cached);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        return devices;
    }
    private static (uint, ControllerInput)? ReadState(int index)
    {
        if (XInputGetState((uint)index, out var state) != 0) return null;
        var pad = state.Pad;
        return (state.Packet, new(pad.LX / 32768f, pad.LY / 32768f, pad.RX / 32768f, pad.RY / 32768f, pad.LT / 255f, pad.RT / 255f, pad.Buttons));
    }
    private static (string, int) ReadBattery(int index)
    {
        if (XInputGetBatteryInformation((uint)index, 0, out var battery) == 0)
        {
            if (battery.Type == 1) return ("XInput · Wired", -1);
            if (battery.Type is 2 or 3) return ("XInput · Wireless", Math.Clamp(battery.Level, (byte)0, (byte)3) * 100 / 3);
        }
        return ("XInput · Windows controller", -1);
    }
    public async Task RumbleAsync(int slot, CancellationToken token)
    {
        if (slot is < 8 or > 11) throw new ArgumentOutOfRangeException(nameof(slot));
        token.ThrowIfCancellationRequested();
        var vibration = new Vibration { Left = 24000, Right = 16000 };
        if (XInputSetState((uint)(slot - 8), ref vibration) != 0) throw new IOException("The Windows controller disconnected. Reconnect it and try again.");
        try { await Task.Delay(400, token); }
        finally { vibration = default; XInputSetState((uint)(slot - 8), ref vibration); }
    }
}
