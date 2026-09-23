using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// <see cref="IHttpProbe"/> with the router tester's semantics (net.uc classify): an HTTP error status counts as
/// reachable when no minimum size is asked, otherwise it is http_error; a short body is too_small. With a local port
/// range every connection is bound to a free source port from it (isolated automatic selection, ARCHITECTURE-WIN §7).
/// </summary>
public sealed class HttpProbe : IHttpProbe
{
    /// <summary>Body read when no minimum size is asked (enough to tell a stub page from a real one).</summary>
    public const long DefaultReadLimit = 1024 * 1024;
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    public async Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        int? status = null;
        var tls = new TlsObservation();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ProbeResult(request.Url, false, 0, 0, "local_error", null);
        if (request.LocalPortFrom is { } pf && (request.LocalPortTo is not { } pt || pf < 1 || pt > 65535 || pf > pt))
            return new ProbeResult(request.Url, false, 0, 0, "local_error", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(request.Timeout);
        using var handler = CreateHandler(request.LocalPortFrom, request.LocalPortTo, tls);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            // HTTP/1.1 over TCP only: HTTP/3 (QUIC, UDP) would bypass the isolation filter of the local port range
            using var msg = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            msg.Headers.UserAgent.ParseAdd(UserAgent);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            long limit = request.MinBytes > 0 ? request.MinBytes : DefaultReadLimit;
            await using var body = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var buf = new byte[65536];
            int n;
            // reaching MinBytes is all the check needs: stop there
            while (bytes < limit && (n = await body.ReadAsync(buf, timeoutCts.Token).ConfigureAwait(false)) > 0)
                bytes += n;
            long ms = sw.ElapsedMilliseconds;
            if (!resp.IsSuccessStatusCode)
                return request.MinBytes == 0
                    ? new ProbeResult(request.Url, true, ms, bytes, null, status)
                    : new ProbeResult(request.Url, false, ms, bytes, "http_error", status);
            if (bytes < request.MinBytes)
                return new ProbeResult(request.Url, false, ms, bytes, "too_small", status);
            return new ProbeResult(request.Url, true, ms, bytes, null, status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(request.Url, false, sw.ElapsedMilliseconds, bytes, "timeout", status);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or AuthenticationException or SocketException)
        {
            return new ProbeResult(request.Url, false, sw.ElapsedMilliseconds, bytes, Classify(e, tls), status);
        }
    }

    public async Task<byte[]> DownloadAsync(string url, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("only http(s) URLs can be downloaded", nameof(url));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var handler = CreateHandler(null, null, null);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        try
        {
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException($"download is larger than {maxBytes} bytes");
            await using var body = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var ms = new MemoryStream();
            var buf = new byte[65536];
            int n;
            while ((n = await body.ReadAsync(buf, timeoutCts.Token).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > maxBytes)
                    throw new InvalidDataException($"download is larger than {maxBytes} bytes");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"download timed out after {timeout.TotalSeconds:0} s", e);
        }
    }

    /// <summary>Maps a failed request to the router's error codes.</summary>
    internal static string Classify(Exception e, TlsObservation? tls)
    {
        if (tls?.CertificateRejected == true)
            return "tls_cert";
        if (HasReset(e))
            return "reset";
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is LocalPortException)
                return "local_error";
            if (x is SocketException se)
            {
                switch (se.SocketErrorCode)
                {
                    case SocketError.ConnectionReset:
                    case SocketError.ConnectionAborted:
                        return "reset";
                    case SocketError.TimedOut:
                        return "timeout";
                    case SocketError.ConnectionRefused:
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                    case SocketError.TryAgain:
                    case SocketError.AddressNotAvailable:
                        return "connect_failed";
                }
            }
            if (x is HttpRequestException hre)
            {
                switch (hre.HttpRequestError)
                {
                    case HttpRequestError.NameResolutionError:
                    case HttpRequestError.ConnectionError when x.InnerException is not LocalPortException:
                        return "connect_failed";
                    case HttpRequestError.SecureConnectionError:
                        return "tls_error";
                    case HttpRequestError.ResponseEnded:
                        return "reset";
                }
            }
            if (x is AuthenticationException)
                return "tls_error";
        }
        return "failed";
    }

    private static bool HasReset(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
                return true;
        return false;
    }

    internal static SocketsHttpHandler CreateHandler(int? portFrom, int? portTo, TlsObservation? tls)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
        };
        if (tls is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;
                    tls.CertificateRejected = true;
                    return false;
                },
            };
        }
        if (portFrom is { } from && portTo is { } to)
            handler.ConnectCallback = (ctx, token) => ConnectFromRangeAsync(ctx.DnsEndPoint, from, to, token);
        return handler;
    }

    /// <summary>Connects from a free local port in [from, to]; a random start spreads parallel probes.</summary>
    internal static async ValueTask<Stream> ConnectFromRangeAsync(DnsEndPoint target, int from, int to, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        int count = to - from + 1;
        int start = Random.Shared.Next(count);
        for (int i = 0; i < count; i++)
        {
            int port = from + (start + i) % count;
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, port));
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                socket.Dispose();
                continue;
            }
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, target.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // same 4-tuple still in TIME_WAIT: Bind passes, connect fails (SPIKE-M1 §5) — take the next port
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new LocalPortException($"no free local port in {from}-{to}");
    }

    internal sealed class TlsObservation
    {
        public volatile bool CertificateRejected;
    }

    internal sealed class LocalPortException(string message) : IOException(message);
}
