using System.Diagnostics;
using System.Runtime.InteropServices;

// Opt-in diagnostics for the calling test process only. No memory contents,
// page locking, working-set trimming or changes to allocation protections.
internal static class NativeMemoryMap
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RegionInfo
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, Alignment;
        public ulong RegionSize;
        public uint State, Protect, Type, Alignment2;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nuint address, out RegionInfo info, nuint length);
    private sealed record Region(ulong Allocation, ulong Bytes, uint Type, uint Protection);
    internal static object Capture()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return new { available = false };
        if (Marshal.SizeOf<RegionInfo>() != 48) throw new InvalidOperationException("Unexpected x64 MEMORY_BASIC_INFORMATION layout.");
        var started = Stopwatch.GetTimestamp();
        var committed = new List<Region>();
        var address = 0UL;
        long reservedBytes = 0;
        var queried = 0;
        var complete = false;
        for (; queried < 100_000; queried++)
        {
            var returned = VirtualQuery((nuint)address, out var info, 48);
            if (returned == 0)
            {
                if (Marshal.GetLastWin32Error() != 87) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                complete = true; break; // Above the highest accessible process address.
            }
            if (returned != 48 || info.RegionSize == 0 || info.BaseAddress + info.RegionSize <= address)
                throw new InvalidOperationException("Invalid virtual-memory region enumeration.");
            if (info.State == 0x1000) committed.Add(new(info.AllocationBase, info.RegionSize, info.Type, info.Protect));
            else if (info.State == 0x2000) reservedBytes += (long)info.RegionSize;
            address = info.BaseAddress + info.RegionSize;
        }
        string Kind(uint type) => type switch { 0x20000 => "Private", 0x40000 => "Mapped", 0x1000000 => "Image", _ => "Other" };
        return new
        {
            available = true, complete, queried, reservedBytes, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            scope = "VirtualQuery in the calling x64 process. Committed virtual pages are not resident physical RAM. Copy-on-write image/mapped pages retain their original type, so MEM_PRIVATE totals are not identical to process Private Bytes. Allocation identifiers are only for correlating snapshots of this short-lived diagnostic process. The map does not assign private regions to .NET, native heaps or graphics owners.",
            committedByType = committed.GroupBy(region => region.Type).Select(group => new { type = Kind(group.Key), bytes = group.Sum(region => (long)region.Bytes), regions = group.Count() }),
            privateByProtection = committed.Where(region => region.Type == 0x20000).GroupBy(region => region.Protection)
                .Select(group => new { protection = $"0x{group.Key:X}", bytes = group.Sum(region => (long)region.Bytes), regions = group.Count() }),
            largestPrivateAllocations = committed.Where(region => region.Type == 0x20000).GroupBy(region => region.Allocation)
                .Select(group => new { allocation = $"0x{group.Key:X}", committedBytes = group.Sum(region => (long)region.Bytes), regions = group.Count() })
                .OrderByDescending(group => group.committedBytes).Take(24)
        };
    }
}
