using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Zaprett.Core.Platform;
using Zaprett.Core.Text;

namespace Zaprett.Core.Checks;

/// <summary>A download or check failure with a code of the router's closed list (or "too_large" for a size limit);
/// the service may throw it from <see cref="IHttpProbe.DownloadAsync"/> to give the exact code.</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public DownloadException() : this("failed", "download failed")
    {
    }

    public DownloadException(string message) : this("failed", message)
    {
    }

    public DownloadException(string message, Exception inner) : this("failed", message, inner)
    {
    }

    public string Code { get; }
}

/// <summary>Error codes of checks and downloads (router contract §14.3, ARCHITECTURE-WIN §7) and the classifier that
/// turns .NET exceptions and probe results into them.</summary>
public static class ProbeErrors
{
    public static readonly IReadOnlyList<string> Codes =
        ["timeout", "reset", "tls_cert", "tls_error", "connect_failed", "http_error", "too_small", "local_error", "failed"];

    /// <summary>Text of a code in the current language (resource key probe.err.&lt;code&gt;); an unknown code as is.</summary>
    public static string TextOf(string? code) =>
        code == null ? T.S("probe.err.unknown") : Codes.Contains(code) || code == "too_large" ? T.S("probe.err." + code) : code;

    /// <summary>The router semantics applied to a result of the platform: success needs at least minBytes; an HTTP error
    /// status means the server answered, which counts as reachable when minBytes is 0; unknown codes become "failed".</summary>
    public static ProbeResult Normalize(ProbeResult r, long minBytes)
    {
        if (r.Ok)
            return r.Bytes >= minBytes ? r with { Error = null } : r with { Ok = false, Error = "too_small" };
        if (r.Error == "http_error" && r.HttpStatus != null && minBytes == 0)
            return r with { Ok = true, Error = null };
        if (r.Error == null || !Codes.Contains(r.Error))
            return r with { Error = "failed" };
        return r;
    }

    /// <summary>Code of an exception from a download or check. A cancellation requested by the caller is not a network
    /// error: the caller must check its own token before classifying.</summary>
    public static string Classify(Exception e)
    {
        switch (e)
        {
            case DownloadException d:
                return Codes.Contains(d.Code) || d.Code == "too_large" ? d.Code : "failed";
            case TimeoutException:
            case OperationCanceledException:
                return "timeout";
            case AuthenticationException ae:
                return IsCertificate(ae) ? "tls_cert" : "tls_error";
            case SocketException se:
                return Socket(se.SocketErrorCode);
            case UnauthorizedAccessException:
                return "local_error";
            case HttpRequestException he:
                if (he.StatusCode != null)
                    return "http_error";
                if (he.InnerException != null && he.InnerException is not HttpRequestException)
                {
                    var inner = Classify(he.InnerException);
                    if (inner != "failed")
                        return inner;
                }
                return he.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => "connect_failed",
                    HttpRequestError.SecureConnectionError => "tls_error",
                    HttpRequestError.ResponseEnded => "reset",
                    _ => "failed",
                };
            case IOException io:
                if (io.InnerException is SocketException ise)
                    return Socket(ise.SocketErrorCode);
                if (io.InnerException is AuthenticationException iae)
                    return IsCertificate(iae) ? "tls_cert" : "tls_error";
                return io.InnerException == null ? "local_error" : "failed";
            default:
                return "failed";
        }
    }

    static bool IsCertificate(AuthenticationException e) =>
        e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    static string Socket(SocketError code) => code switch
    {
        SocketError.TimedOut => "timeout",
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => "reset",
        SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostNotFound
            or SocketError.HostDown or SocketError.NetworkDown or SocketError.AddressNotAvailable or SocketError.TryAgain
            or SocketError.NoData => "connect_failed",
        _ => "failed",
    };

    /// <summary>HTTP status of an exception, when it has one.</summary>
    public static int? HttpStatus(Exception e) => e is HttpRequestException { StatusCode: HttpStatusCode s } ? (int)s : null;
}
