using System.Text.Json.Nodes;

namespace Zaprett.Core.Platform;

// Optional platform services the core can use when the service provides them. They are passed to the
// CommandDispatcher constructor separately, so PlatformServices (IPlatform.cs) keeps its signature.

/// <summary>Network List Manager: networks that are not domain-authenticated (names or GUIDs), for --nlm-filter when
/// network_filter.skip_corporate is on (ARCHITECTURE-WIN §5). An empty list means the filter is not added.</summary>
public interface INetworkList
{
    Task<IReadOnlyList<string>> GetNonCorporateNetworksAsync(CancellationToken ct);
}

/// <summary>Program updates (update.check, update.install — ARCHITECTURE-WIN §6, W-7): signed manifest and MSI,
/// implemented by the service. Answers are in the router format ({ok, ...} / {ok:false, error, message}).</summary>
public interface IUpdateControl
{
    Task<JsonObject> CheckAsync(string channel, CancellationToken ct);
    Task<JsonObject> InstallAsync(string channel, CancellationToken ct);
}
