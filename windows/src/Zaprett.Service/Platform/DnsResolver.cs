using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary><see cref="IDnsResolver"/>: system resolver, DNS-over-HTTPS JSON API (dns.google, then Cloudflare), TCP connect.</summary>
public sealed class DnsResolver : IDnsResolver, IDisposable
{
    public static readonly IReadOnlyList<string> DohEndpoints =
    [
        "https://dns.google/resolve",
        "https://cloudflare-dns.com/dns-query",
    ];

    private static readonly TimeSpan DohTimeout = TimeSpan.FromSeconds(8);
    private const int MaxDohBytes = 65536;
    private readonly HttpClient _http;

    public DnsResolver(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = DohTimeout,
            MaxResponseContentBufferSize = MaxDohBytes,
        };
    }

    public async Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct)
    {
        ValidateHost(host);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addrs.Select(a => a.ToString()).Distinct().ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> ResolveDohAsync(string host, CancellationToken ct)
    {
        ValidateHost(host);
        Exception? last = null;
        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseDohJson(text);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException
                                          && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw new HttpRequestException("DNS-over-HTTPS failed: " + last?.Message, last);
    }

    /// <summary>A records of a DoH JSON answer ({"Status":0,"Answer":[{"type":1,"data":"1.2.3.4"}]}); NXDOMAIN → empty.</summary>
    public static IReadOnlyList<string> ParseDohJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("DoH answer is not an object");
        var list = new List<string>();
        if (root["Answer"] is not JsonArray answers)
            return list;
        foreach (var a in answers)
        {
            if (a is JsonObject o && o["type"] is JsonValue t && t.TryGetValue<int>(out var type) && type == 1 &&
                o["data"] is JsonValue d && d.TryGetValue<string>(out var data) &&
                IPAddress.TryParse(data, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                list.Add(ip.ToString());
        }
        return list.Distinct().ToList();
    }

    public async Task<bool> TcpConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            throw new ArgumentException("not an IP address: " + ip, nameof(ip));
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        using var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addr, port), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("invalid host name: " + host, nameof(host));
    }

    public void Dispose() => _http.Dispose();
}
