using System.IO;

namespace Steamy.Services;

public interface IPcGameScanService
{
    Task<GameScanResult> ScanAsync(IProgress<GameScanProgress>? progress = null, CancellationToken token = default, IReadOnlyList<string>? roots = null);
}

public sealed class PcGameScanService(IDownloadedGameLibrary library) : IPcGameScanService
{
    public async Task<GameScanResult> ScanAsync(IProgress<GameScanProgress>? progress = null, CancellationToken token = default, IReadOnlyList<string>? roots = null)
    {
        var result = await Task.Run(() => PcGameScanner.Scan(roots ?? LocalDrives(), progress, token)).ConfigureAwait(false);
        // Keep completed findings when the user cancels a long traversal.
        await library.ImportAsync(result.Games, CancellationToken.None).ConfigureAwait(false);
        return result;
    }
    private static IReadOnlyList<string> LocalDrives() => DriveInfo.GetDrives().Where(d =>
    {
        try { return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; }
        catch (IOException) { return false; }
    }).Select(d => d.RootDirectory.FullName).ToArray();
}
