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
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct Battery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)] private struct Vibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte device, out Battery battery);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref Vibration vibration);

    public IReadOnlyList<ControllerDevice> Read()
    {
        var devices = new List<ControllerDevice>();
        try
        {
            for (uint index = 0; index < 4; index++)
            {
                if (XInputGetState(index, out var state) != 0) continue;
                var pad = state.Pad;
                var batteryPercent = -1;
                var connection = "XInput · Windows controller";
                if (XInputGetBatteryInformation(index, 0, out var battery) == 0)
                {
                    if (battery.Type == 1) connection = "XInput · Wired";
                    else if (battery.Type is 2 or 3) { connection = "XInput · Wireless"; batteryPercent = Math.Clamp(battery.Level, (byte)0, (byte)3) * 100 / 3; }
                }
                // Keep local XInput slots separate from the mapper's 0–7 slots.
                devices.Add(new(8 + (int)index, "xinput:" + index, $"XInput Controller #{index + 1}", connection, batteryPercent, false,
                    new(pad.LX / 32768f, pad.LY / 32768f, pad.RX / 32768f, pad.RY / 32768f, pad.LT / 255f, pad.RT / 255f, pad.Buttons)));
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        return devices;
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
