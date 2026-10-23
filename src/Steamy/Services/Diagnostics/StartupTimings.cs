using System.Diagnostics;
using System.IO;

namespace Steamy.Services;

/// <summary>Opt-in diagnostics; normal launches perform no timing I/O.</summary>
internal static class StartupTimings
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("STEAMY_STARTUP_TRACE");
    internal static void CatalogRequest()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        // Method names only: queries, account data and arguments never enter the trace.
        var callers = new StackTrace().GetFrames().Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.Namespace?.StartsWith("Steamy", StringComparison.Ordinal) == true)
            .Select(method => $"{method!.DeclaringType!.Name}.{method.Name}");
        Mark("CatalogRequest " + string.Join(" <- ", callers));
    }
    internal static void Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            File.AppendAllText(Output, $"{(DateTime.Now - process.StartTime).TotalMilliseconds:F1}\t{phase}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
