using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Steamy.Services;

internal sealed record DiscordAuthorizationCode(string Code, string Redirect);

/// <summary>
/// Standard Discord OAuth2 code approval with SDK-generated PKCE. Used only when
/// the desktop RPC approval transport fails; tokens are still exchanged by the SDK.
/// https://docs.discord.com/developers/topics/oauth2
/// </summary>
internal sealed class DiscordBrowserAuthorization(Action<Uri>? openBrowser = null, int port = 80)
{
    public async Task<DiscordAuthorizationCode> AuthorizeAsync(ulong applicationId, string scopes, string challenge, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            try { listener.Start(); }
            catch (SocketException)
            { throw new DiscordSocialException("Browser sign-in couldn't open its localhost callback. Close other Steamy sign-in windows and try again; localhost port 80 must be available."); }
            var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = new UriBuilder("http", "127.0.0.1", localPort, "/callback").Uri;
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var query = new Dictionary<string, string>
            {
                ["client_id"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["response_type"] = "code", ["redirect_uri"] = redirect.AbsoluteUri,
                ["scope"] = scopes, ["state"] = state, ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256", ["prompt"] = "consent"
            };
            var approval = new Uri("https://discord.com/oauth2/authorize?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            try
            {
                if (openBrowser is not null) openBrowser(approval);
                else Process.Start(new ProcessStartInfo(approval.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            { throw new DiscordSocialException("The sign-in browser couldn't open. Check your default browser and try again."); }

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                connection.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                string? request;
                try { request = await ReadHeaderAsync(stream, connection.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); continue; }
                if (request is null) continue;
                var lines = request.Split("\r\n", StringSplitOptions.None);
                var first = lines[0].Split(' ');
                var hosts = lines.Skip(1).Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (first.Length != 3 || first[0] != "GET" || !first[1].StartsWith('/') || hosts.Length != 1
                    || !IsLocalHost(hosts[0][5..].Trim(), localPort)
                    || !Uri.TryCreate("http://127.0.0.1" + first[1], UriKind.Absolute, out var callback)
                    || callback.AbsolutePath != "/callback")
                { await ReplyAsync(stream, 400, "Invalid sign-in callback.", connection.Token).ConfigureAwait(false); continue; }
                var parameters = ParseQuery(callback.Query);
                if (parameters is null || !parameters.TryGetValue("state", out var returnedState) || !SameState(state, returnedState))
                { await ReplyAsync(stream, 400, "This sign-in callback does not match. Return to Steamy and try again.", connection.Token).ConfigureAwait(false); continue; }
                if (parameters.ContainsKey("error"))
                {
                    await ReplyAsync(stream, 200, "Discord sign-in was not approved. You can return to Steamy.", connection.Token).ConfigureAwait(false);
                    throw new DiscordSocialException("Discord browser approval was declined or failed. You can connect again whenever you're ready.");
                }
                if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 2048 || code.Any(char.IsControl))
                { await ReplyAsync(stream, 400, "Discord did not return a valid approval code.", connection.Token).ConfigureAwait(false); continue; }
                await ReplyAsync(stream, 200, "Discord approval received. You can return to Steamy.", connection.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new(code, redirect.AbsoluteUri);
            }
        }
        finally { listener.Stop(); }
    }

    private static bool IsLocalHost(string host, int port) => host.Equals("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) || port == 80 && host == "127.0.0.1";
    private static bool SameState(string expected, string actual) => expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || result.Count >= 16 || !result.TryAdd(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair[1]))) return null;
        }
        return result;
    }
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new byte[16384]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
            var end = bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Encoding.ASCII.GetString(bytes, 0, end);
        }
        return null;
    }
    private static async Task ReplyAsync(NetworkStream stream, int status, string text, CancellationToken token)
    {
        // Never echo the callback URL, code, state or error description into the page.
        var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Steamy · Discord</title></head><body><h1>Steamy</h1><p>" + text + "</p></body></html>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
    }
}
