// GPL-2.0; see LICENSE. Steamy modification, 2026-10-02.
using System;
using System.Diagnostics;
using System.Globalization;

namespace DepotDownloader;

/// <summary>Reports validated content and completed CDN chunks, never reserved file lengths.</summary>
internal static class SteamyProgress
{
    public static bool Enabled { get; set; }
    private static readonly object Gate = new();
    private static long lastReport;
    public static void Report(uint depot, string phase, ulong content, ulong total, ulong transferred,
        long transferTotal, ulong expanded, bool force = false)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds < 100) return;
            lastReport = now;
            Console.WriteLine(Format(depot, phase, content, total, transferred, transferTotal,
                content >= expanded ? content - expanded : 0));
        }
    }
    internal static string Format(uint depot, string phase, ulong content, ulong total,
        ulong transferred, long transferTotal, ulong reused) => string.Create(CultureInfo.InvariantCulture,
            $"STEAMY_PROGRESS|1|{depot}|{phase}|{content}|{total}|{transferred}|{transferTotal}|{reused}");
}
