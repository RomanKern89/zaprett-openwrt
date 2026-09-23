using System.Text.Json.Nodes;

namespace Zaprett.Ipc;

/// <summary>Service event pushed after "subscribe" (ARCHITECTURE-WIN §6): type is status|job|probe|monitor.</summary>
public sealed record ServiceEvent(string Type, JsonObject Data);

/// <summary>
/// Client side of the \\.\pipe\zaprett JSON-RPC protocol, used by the UI and the CLI.
/// The UI is developed against this interface (with a fake implementation), the real one talks to the service.
/// </summary>
public interface IZaprettClient : IAsyncDisposable
{
    /// <summary>Calls a method (e.g. "status", "wizard.apply") and returns the router-compatible answer object.
    /// Transport failures (service not running, pipe broken) throw ZaprettUnavailableException.</summary>
    Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default);

    /// <summary>Stream of service events until cancelled or the connection drops.</summary>
    IAsyncEnumerable<ServiceEvent> SubscribeAsync(CancellationToken ct = default);
}

public sealed class ZaprettUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
