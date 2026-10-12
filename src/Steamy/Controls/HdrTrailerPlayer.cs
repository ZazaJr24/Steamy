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
    public event EventHandler? PreviewActivated;
    public event EventHandler? SoundRequested;
    public event EventHandler? FallbackRequested;
    public bool IsPlaying { get; private set; }

    public HdrTrailerPlayer()
    {
        // Native controls handle sound and forward card activation to WPF.
        IsEnabled = true; IsHitTestVisible = true; Focusable = false;
        DefaultBackgroundColor = System.Drawing.Color.Transparent;
    }

    public async Task StartAsync(Uri source, CancellationToken cancellationToken)
    {
        if (!source.IsFile) throw new ArgumentException("HDR previews must come from the bounded local trailer cache.", nameof(source));
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Steamy.Storage.AppDataPaths.Root, "TrailerBrowser"), new CoreWebView2EnvironmentOptions { Language = "en-US" });
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
            else if (message == "sound") SoundRequested?.Invoke(this, EventArgs.Empty);
            else if (message == "open") PreviewActivated?.Invoke(this, EventArgs.Empty);
            else if (message == "fallback") RequestFallback();
        };
        core.SetVirtualHostNameToFolderMapping("steamy-trailer.invalid", Path.GetDirectoryName(source.LocalPath)!, CoreWebView2HostResourceAccessKind.DenyCors);
        var url = JsonSerializer.Serialize("https://steamy-trailer.invalid/" + Uri.EscapeDataString(Path.GetFileName(source.LocalPath)));
        // Chromium chooses the display color space and decoder. Never force a color profile,
        // convert SDR to HDR, or disable hardware acceleration to manufacture an HDR label.
        core.NavigateToString($$"""
            <!doctype html><html lang="en"><head><meta name="color-scheme" content="dark">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; media-src https://steamy-trailer.invalid https://steamy-audio.invalid; script-src 'unsafe-inline'; style-src 'unsafe-inline'">
            <style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent;cursor:pointer}video{width:100%;height:100%;object-fit:cover;border-radius:14px;opacity:0}button{position:absolute;top:8px;right:8px;padding:5px 9px;border:0;border-radius:5px;background:#191d23dd;color:white;font:11px Segoe UI;cursor:pointer}button:focus-visible{outline:2px solid #9bbbff}</style></head>
            <body><video muted autoplay playsinline></video><audio muted></audio><button type="button" title="Unmute trailer" aria-label="Unmute trailer">Unmute</button><script>
            const v=document.querySelector('video'), hdr=matchMedia('(dynamic-range: high)');
            const a=document.querySelector('audio'), sound=document.querySelector('button');
            sound.addEventListener('click',e=>{e.stopPropagation();v.muted=!v.muted;a.muted=v.muted;if(!v.muted){chrome.webview.postMessage('sound');if(a.src){a.currentTime=v.currentTime;a.play().catch(()=>{})} }sound.textContent=v.muted?'Unmute':'Mute';sound.setAttribute('aria-label',v.muted?'Unmute trailer':'Mute trailer');sound.title=sound.getAttribute('aria-label')});
            document.addEventListener('click',()=>chrome.webview.postMessage('open'));
            const fallback=()=>chrome.webview.postMessage('fallback');
            if(!hdr.matches) fallback();
            else {
              hdr.addEventListener('change',e=>{if(!e.matches) fallback()});
              v.src={{url}};v.playbackRate=1;
              v.addEventListener('error',fallback);
              v.addEventListener('loadeddata',()=>{v.currentTime=3;v.style.opacity=1;chrome.webview.postMessage('ready');v.play().catch(fallback)},{once:true});
              v.addEventListener('timeupdate',()=>{if(v.currentTime>=21)v.currentTime=3;if(a.src&&Math.abs(a.currentTime-v.currentTime)>.7)a.currentTime=v.currentTime});
              v.addEventListener('ended',()=>{v.currentTime=0;v.play().catch(fallback)});
            }
            </script></body></html>
            """);
        _ = WatchForReadyAsync(cancellationToken);
    }

    public async Task SetAudioSourceAsync(Uri source)
    {
        if (_closed || !source.IsFile || CoreWebView2 is null) return;
        CoreWebView2.SetVirtualHostNameToFolderMapping("steamy-audio.invalid", Path.GetDirectoryName(source.LocalPath)!, CoreWebView2HostResourceAccessKind.DenyCors);
        var url = JsonSerializer.Serialize("https://steamy-audio.invalid/" + Uri.EscapeDataString(Path.GetFileName(source.LocalPath)));
        await CoreWebView2.ExecuteScriptAsync($"a.src={url};a.addEventListener('loadeddata',()=>{{a.currentTime=v.currentTime;a.muted=v.muted;if(!v.muted)a.play().catch(()=>{{}})}},{{once:true}});");
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
