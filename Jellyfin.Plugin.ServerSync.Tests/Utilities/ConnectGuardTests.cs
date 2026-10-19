using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The connect callbacks behind the source clients. The URL check runs when an entry is saved, but a
/// name can resolve elsewhere later (DNS rebinding), so every connection classifies the addresses the
/// name resolves to at that moment. Run against a real listener on loopback.
/// </summary>
public sealed class ConnectGuardTests : IDisposable
{
    private readonly TcpListener _listener;
    private int _accepted;

    public ConnectGuardTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptAsync();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose() => _listener.Stop();

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response);
            }
        }
    }

    private static HttpClient ClientWith(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<System.IO.Stream>> connect)
        => new(new SocketsHttpHandler { ConnectCallback = connect, AllowAutoRedirect = false });

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(' ');
        }

        return text.ToString();
    }

    /// <summary>
    /// A host name that resolves to loopback is refused by the public-only client before any
    /// connection is made, and the error names the address it resolved to.
    /// True: a name saved while it pointed at a public address cannot later be pointed at this server.
    /// False: rebinding the name reaches services on the plugin's own host with the stored key.
    /// </summary>
    [Fact]
    public async Task PublicOnly_NameResolvingToLoopback_IsRefusedBeforeConnecting()
    {
        using var client = ClientWith(ConfigurationUtilities.ConnectPublicOnlyAsync);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://localhost:{Port}/System/Info")));

        var message = Flatten(ex);
        Assert.Contains("'localhost' resolves to", message, StringComparison.Ordinal);
        Assert.Contains("not allowed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref _accepted));
    }

    /// <summary>
    /// A loopback address literal is refused by the public-only client too.
    /// True: the address and a name for it are judged alike.
    /// False: writing the address instead of the name gets around the guard.
    /// </summary>
    [Fact]
    public async Task PublicOnly_LoopbackLiteral_IsRefused()
    {
        using var client = ClientWith(ConfigurationUtilities.ConnectPublicOnlyAsync);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{Port}/System/Info")));

        Assert.Equal(0, Volatile.Read(ref _accepted));
    }

    /// <summary>
    /// The client for entries that allow private networks connects to the same name.
    /// True: a LAN or loopback server works when the entry allows it.
    /// False: the guard refuses every private server, not only the ones an entry disallows.
    /// </summary>
    [Fact]
    public async Task PrivateAllowed_NameResolvingToLoopback_Connects()
    {
        using var client = ClientWith(ConfigurationUtilities.ConnectPrivateAllowedAsync);

        using var response = await client.GetAsync(new Uri($"http://localhost:{Port}/System/Info"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Even with private networks allowed, the metadata range is refused at connect time.
    /// True: a name pointed at 169.254.169.254 cannot read cloud credentials through the plugin.
    /// False: rebinding a name to the metadata service reaches it.
    /// </summary>
    [Fact]
    public async Task PrivateAllowed_LinkLocalLiteral_IsRefused()
    {
        using var client = ClientWith(ConfigurationUtilities.ConnectPrivateAllowedAsync);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://169.254.169.254/latest")));

        Assert.Contains("Link-local", Flatten(ex), StringComparison.Ordinal);
    }
}
