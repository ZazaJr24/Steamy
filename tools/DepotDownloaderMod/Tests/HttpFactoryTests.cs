// GPL-2.0; see ../LICENSE. Steamy modification, 2026-10-01.
#if HAVE_PATCHED_FACTORY
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DepotDownloader;

namespace DepotDownloaderMod.Tests;

public sealed class HttpFactoryTests
{
    [Fact]
    public async Task ActualPatchedFactorySharesCapAcrossConcurrentSocketConnections()
    {
        const int rate = 96 * 1024;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/fixture");
        var server = ServeAsync(listener, 3, rate / 3, timeout.Token);
        HttpClientFactory.ConfigureDownloadLimit(rate);
        var timer = Stopwatch.StartNew();
        try
        {
            var readers = Enumerable.Range(0, 3).Select(async _ =>
            {
                using var client = HttpClientFactory.CreateHttpClient();
                using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(timeout.Token);
            });
            var bodies = await Task.WhenAll(readers);
            Assert.All(bodies, body => Assert.Equal(rate / 3, body.Length));
            // Three separate connections must share the process cap, rather than each
            // independently transferring a full-rate payload in roughly 300 ms.
            Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900), timer.Elapsed.ToString());
            await server;
        }
        finally
        {
            HttpClientFactory.ConfigureDownloadLimit(0);
        }
    }

    private static async Task ServeAsync(TcpListener listener, int count, int length, CancellationToken cancellationToken)
    {
        var writers = new List<Task>();
        for (var index = 0; index < count; index++)
        {
            var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            writers.Add(RespondAsync(socket, length, cancellationToken));
        }
        await Task.WhenAll(writers);
    }

    private static async Task RespondAsync(TcpClient socket, int length, CancellationToken cancellationToken)
    {
        using (socket)
        {
            await using var network = socket.GetStream();
            // Consume the request before closing so the peer receives a graceful FIN.
            var request = new byte[4096];
            var requestHeader = new StringBuilder();
            while (!requestHeader.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var received = await network.ReadAsync(request, cancellationToken);
                if (received == 0) return;
                requestHeader.Append(Encoding.ASCII.GetString(request, 0, received));
                if (requestHeader.Length > 16384) throw new InvalidDataException("Oversized fixture request.");
            }
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
            await network.WriteAsync(header, cancellationToken);
            await network.WriteAsync(new byte[length], cancellationToken);
        }
    }
}
#endif
