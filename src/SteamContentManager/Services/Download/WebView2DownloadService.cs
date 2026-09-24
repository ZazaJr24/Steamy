using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SteamContentManager.Services;

public sealed class WebView2DownloadService : IDisposable
{
    private WebView2? _webView;
    private CoreWebView2Environment? _environment;
    private TaskCompletionSource<string>? _downloadTcs;
    private string _downloadFolder = string.Empty;
    private IProgress<GameFixDownloadProgress>? _progress;
    private bool _initialized;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        _downloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "game-fix-downloads");
        Directory.CreateDirectory(_downloadFolder);

        _environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamContentManager",
                "WebView2Cache"));

        _webView = new WebView2();
        await _webView.EnsureCoreWebView2Async(_environment);

        _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

        _initialized = true;
    }

    public async Task<GameFixDownloadResult> DownloadAsync(
        string url,
        string filename,
        IProgress<GameFixDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync();

        var localPath = Path.Combine(_downloadFolder, filename);
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
        {
            var bytes = new byte[4];
            using (var fs = File.OpenRead(localPath))
            {
                if (fs.Read(bytes, 0, 4) >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
                    return new GameFixDownloadResult(true, localPath, $"Already downloaded: {FormatSize(new FileInfo(localPath).Length)}");
            }
            File.Delete(localPath);
        }

        _progress = progress;
        _downloadTcs = new TaskCompletionSource<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        using var reg = cts.Token.Register(() =>
            _downloadTcs.TrySetException(new OperationCanceledException("Download timed out.")));

        _webView!.CoreWebView2.Navigate(url);

        try
        {
            var resultPath = await _downloadTcs.Task;

            if (resultPath != localPath && File.Exists(resultPath))
            {
                if (File.Exists(localPath)) File.Delete(localPath);
                File.Move(resultPath, localPath);
            }

            if (!File.Exists(localPath) || new FileInfo(localPath).Length == 0)
                return new GameFixDownloadResult(false, string.Empty, "Download produced an empty file.");

            return new GameFixDownloadResult(true, localPath, $"Downloaded {FormatSize(new FileInfo(localPath).Length)}.");
        }
        catch (OperationCanceledException)
        {
            return new GameFixDownloadResult(false, string.Empty, "Download cancelled or timed out.");
        }
        catch (Exception ex)
        {
            return new GameFixDownloadResult(false, string.Empty, $"Download failed: {ex.Message}");
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var filename = Path.GetFileName(e.ResultFilePath);
        var targetPath = Path.Combine(_downloadFolder, filename);
        e.ResultFilePath = targetPath;
        e.Handled = true;

        e.DownloadOperation.BytesReceivedChanged += (s, _) =>
        {
            if (s is not CoreWebView2DownloadOperation op) return;
            var received = op.BytesReceived;
            var total = op.TotalBytesToReceive;
            var percent = total > 0 ? 100.0 * (double)received / (double)total : 0;

            _progress?.Report(new GameFixDownloadProgress(
                Math.Clamp(percent, 0, 100),
                filename,
                FormatSize(received),
                total > 0 ? FormatSize((long)total) : "—",
                "—",
                total > 0 && received > 0
                    ? $"{((double)total - (double)received) / Math.Max((double)received, 1.0):F0}s"
                    : "—"));
        };

        e.DownloadOperation.StateChanged += (s, _) =>
        {
            if (s is not CoreWebView2DownloadOperation op) return;
            switch (op.State)
            {
                case CoreWebView2DownloadState.Completed:
                    _downloadTcs?.TrySetResult(targetPath);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    _downloadTcs?.TrySetException(new IOException($"Download interrupted: {op.InterruptReason}"));
                    break;
            }
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _webView?.Dispose();
    }
}
