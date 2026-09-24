using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Zaprett.Core.Platform;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>Local TCP server with a per-connection script; records the client ports it saw.</summary>
internal sealed class ScriptedServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public ConcurrentQueue<int> ClientPorts { get; } = new();
    public int Port { get; }

    public ScriptedServer(Func<Socket, CancellationToken, Task> handler)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket s;
                try
                {
                    s = await _listener.AcceptSocketAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
                ClientPorts.Enqueue(((IPEndPoint)s.RemoteEndPoint!).Port);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(s, _cts.Token);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        s.Dispose();
                    }
                });
            }
        });
    }

    public static async Task ReadRequestAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n"))
        {
            int n = await s.ReceiveAsync(buf, ct);
            if (n == 0)
                return;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
    }

    public static Func<Socket, CancellationToken, Task> Respond(int status, int bodyBytes) => async (s, ct) =>
    {
        await ReadRequestAsync(s, ct);
        string head = $"HTTP/1.1 {status} X\r\nContent-Length: {bodyBytes}\r\nConnection: close\r\n\r\n";
        await s.SendAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.SendAsync(new byte[bodyBytes], ct);
        s.Shutdown(SocketShutdown.Send);
        await Task.Delay(200, ct);
    };

    public static void Reset(Socket s)
    {
        s.LingerState = new LingerOption(true, 0);
        s.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        await _loop;
        _cts.Dispose();
    }
}

public class HttpProbeTests
{
    private static readonly HttpProbe Probe = new();

    // generous by default: under a full parallel test run (engines being started, certificates made) 3 s was not
    // always enough for a local TLS handshake; only SilentServer_IsTimeout tests the timeout itself (500 ms)
    private static Task<ProbeResult> Run(string url, long min = 0, int timeoutMs = 10000, int? from = null, int? to = null) =>
        Probe.ProbeAsync(new ProbeRequest(url, min, TimeSpan.FromMilliseconds(timeoutMs), from, to), default);

    [Fact]
    public async Task FullBody_IsOk()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 5000));
        var r = await Run($"http://127.0.0.1:{srv.Port}/", min: 4000);
        Assert.True(r.Ok, r.Error);
        Assert.Null(r.Error);
        Assert.Equal(200, r.HttpStatus);
        Assert.True(r.Bytes >= 4000);
    }

    [Fact]
    public async Task ShortBody_IsTooSmall()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 100));
        var r = await Run($"http://127.0.0.1:{srv.Port}/", min: 1000);
        Assert.False(r.Ok);
        Assert.Equal("too_small", r.Error);
        Assert.Equal(100, r.Bytes);
    }

    [Fact]
    public async Task NotFound_IsHttpErrorOnlyWhenSizeMatters()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(404, 10));
        var withMin = await Run($"http://127.0.0.1:{srv.Port}/", min: 1000);
        Assert.Equal("http_error", withMin.Error);
        Assert.Equal(404, withMin.HttpStatus);
        var noMin = await Run($"http://127.0.0.1:{srv.Port}/", min: 0);
        Assert.True(noMin.Ok);
        Assert.Equal(404, noMin.HttpStatus);
    }

    [Fact]
    public async Task SilentServer_IsTimeout()
    {
        await using var srv = new ScriptedServer(async (s, ct) => await Task.Delay(Timeout.Infinite, ct));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Run($"http://127.0.0.1:{srv.Port}/", timeoutMs: 500);
        Assert.Equal("timeout", r.Error);
        Assert.InRange(sw.ElapsedMilliseconds, 400, 5000);
    }

    [Fact]
    public async Task ResetAfterRequest_IsReset()
    {
        await using var srv = new ScriptedServer(async (s, ct) =>
        {
            await ScriptedServer.ReadRequestAsync(s, ct);
            ScriptedServer.Reset(s);
        });
        var r = await Run($"http://127.0.0.1:{srv.Port}/");
        Assert.Equal("reset", r.Error);
    }

    [Fact]
    public async Task ResetDuringTlsHandshake_IsReset()
    {
        await using var srv = new ScriptedServer(async (s, ct) =>
        {
            await s.ReceiveAsync(new byte[4096], ct);   // the ClientHello
            ScriptedServer.Reset(s);
        });
        var r = await Run($"https://127.0.0.1:{srv.Port}/");
        Assert.Equal("reset", r.Error);
    }

    [Fact]
    public async Task PlainTextAnswerToTls_IsTlsError()
    {
        await using var srv = new ScriptedServer(async (s, ct) =>
        {
            await s.ReceiveAsync(new byte[4096], ct);
            await s.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), ct);
            await Task.Delay(300, ct);
        });
        var r = await Run($"https://127.0.0.1:{srv.Port}/");
        Assert.Equal("tls_error", r.Error);
    }

    [Fact]
    public async Task UntrustedCertificate_IsTlsCert()
    {
        using var cert = SelfSigned();
        await using var srv = new ScriptedServer(async (s, ct) =>
        {
            await using var ssl = new SslStream(new NetworkStream(s, ownsSocket: false));
            await ssl.AuthenticateAsServerAsync(cert);
            await Task.Delay(300, ct);
        });
        var r = await Run($"https://127.0.0.1:{srv.Port}/");
        Assert.Equal("tls_cert", r.Error);
    }

    [Fact]
    public async Task ClosedPort_IsConnectFailed()
    {
        // bound but not listening: the port stays ours (a listener of a parallel test cannot take it, which a
        // stopped TcpListener allowed) and a connection to it is refused
        using var s = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)s.LocalEndPoint!).Port;
        var r = await Run($"http://127.0.0.1:{port}/");
        Assert.Equal("connect_failed", r.Error);
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    public async Task BadUrl_IsLocalError(string url) => Assert.Equal("local_error", (await Run(url)).Error);

    [Fact]
    public async Task LocalPortRange_IsUsedForEveryConnection()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 10));
        for (int i = 0; i < 5; i++)
        {
            var r = await Run($"http://127.0.0.1:{srv.Port}/", from: 40100, to: 40140);
            Assert.True(r.Ok, r.Error);
        }
        Assert.Equal(5, srv.ClientPorts.Count);
        Assert.All(srv.ClientPorts, p => Assert.InRange(p, 40100, 40140));
    }

    [Fact]
    public async Task WithoutRange_PortsAreEphemeral_NegativeControl()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 10));
        var r = await Run($"http://127.0.0.1:{srv.Port}/");
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(srv.ClientPorts, p => p is >= 40100 and <= 40140);
    }

    [Fact]
    public async Task ExhaustedRange_IsLocalError()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 10));
        // the whole range held by this test (not fixed numbers: another test run on the machine may hold those)
        using var block = PortBlock.Take(3);
        var r = await Run($"http://127.0.0.1:{srv.Port}/", from: block.First, to: block.Last);
        Assert.Equal("local_error", r.Error);
        Assert.Empty(srv.ClientPorts);
    }

    [Fact]
    public async Task Download_EnforcesSizeLimit()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 3000));
        var ok = await Probe.DownloadAsync($"http://127.0.0.1:{srv.Port}/", 5000, TimeSpan.FromSeconds(3), default);
        Assert.Equal(3000, ok.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Probe.DownloadAsync($"http://127.0.0.1:{srv.Port}/", 1000, TimeSpan.FromSeconds(3), default));
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=zaprett-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pfx), null);
    }
}
