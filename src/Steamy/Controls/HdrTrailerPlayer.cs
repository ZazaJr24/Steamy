using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Steamy.Controls;

/// <summary>Native browser HWND preserves HDR video instead of passing it through WPF's SDR surface.</summary>
public sealed class HdrTrailerPlayer : WebView2
{
    private static Task<CoreWebView2Environment>? _environment;
    private bool _closed;
    public event EventHandler? VideoReady;
    public event EventHandler? FallbackRequested;
    public bool IsPlaying { get; private set; }

    public HdrTrailerPlayer()
    {
        // Disabled native child windows pass pointer input to the WPF card beneath them.
        IsEnabled = false; IsHitTestVisible = false; Focusable = false;
        DefaultBackgroundColor = System.Drawing.Color.Transparent;
    }

    public async Task StartAsync(Uri source, CancellationToken cancellationToken)
    {
        if (!source.IsFile) throw new ArgumentException("HDR previews must come from the bounded local trailer cache.", nameof(source));
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Steamy.Storage.AppDataPaths.Root, "TrailerBrowser"));
        var environment = await _environment.WaitAsync(cancellationToken);
        if (_closed) return;
        await EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) return;
        var core = CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        var initialDocument = true;
        core.NavigationStarting += (_, args) =>
        {
            // New runtimes navigate local HTML through a data URI; older ones use about:blank.
            if (initialDocument && (args.Uri == "about:blank" || args.Uri.StartsWith("data:text/html;charset=utf-8;base64,", StringComparison.Ordinal)))
            { initialDocument = false; return; }
            args.Cancel = true;
        };
        core.ProcessFailed += (_, _) => RequestFallback();
        core.WebMessageReceived += (_, args) =>
        {
            if (_closed) return;
            var message = args.TryGetWebMessageAsString();
            if (message == "ready") { IsPlaying = true; VideoReady?.Invoke(this, EventArgs.Empty); }
            else if (message == "fallback") RequestFallback();
        };
        core.SetVirtualHostNameToFolderMapping("steamy-trailer.invalid", Path.GetDirectoryName(source.LocalPath)!, CoreWebView2HostResourceAccessKind.DenyCors);
        var url = JsonSerializer.Serialize("https://steamy-trailer.invalid/" + Uri.EscapeDataString(Path.GetFileName(source.LocalPath)));
        // Chromium chooses the display color space and decoder. Never force a color profile,
        // convert SDR to HDR, or disable hardware acceleration to manufacture an HDR label.
        core.NavigateToString($$"""
            <!doctype html><html><head><meta name="color-scheme" content="dark">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; media-src https://steamy-trailer.invalid; script-src 'unsafe-inline'; style-src 'unsafe-inline'">
            <style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent;pointer-events:none}video{width:100%;height:100%;object-fit:cover;border-radius:14px;opacity:0}</style></head>
            <body><video muted autoplay playsinline></video><script>
            const v=document.querySelector('video'), hdr=matchMedia('(dynamic-range: high)');
            const fallback=()=>chrome.webview.postMessage('fallback');
            if(!hdr.matches) fallback();
            else {
              hdr.addEventListener('change',e=>{if(!e.matches) fallback()});
              v.src={{url}};v.playbackRate=1;
              v.addEventListener('error',fallback);
              v.addEventListener('loadeddata',()=>{v.currentTime=3;v.style.opacity=1;chrome.webview.postMessage('ready');v.play().catch(fallback)},{once:true});
              v.addEventListener('timeupdate',()=>{if(v.currentTime>=21)v.currentTime=3});
              v.addEventListener('ended',()=>{v.currentTime=0;v.play().catch(fallback)});
            }
            </script></body></html>
            """);
        _ = WatchForReadyAsync(cancellationToken);
    }

    private async Task WatchForReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            if (!_closed && !IsPlaying) RequestFallback();
        }
        catch (OperationCanceledException) { }
    }

    private void RequestFallback() => Dispatcher.BeginInvoke(() =>
    {
        // Dispose outside the browser callback to avoid reentering WebView2's event loop.
        if (!_closed) FallbackRequested?.Invoke(this, EventArgs.Empty);
    });

    public void ClosePreview()
    {
        if (_closed) return;
        _closed = true; IsPlaying = false;
        Dispose();
    }
}
