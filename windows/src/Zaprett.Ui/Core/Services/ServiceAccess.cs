using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zaprett.Ui.Core.Services;

/// <summary>
/// Who may change what. The service answers "status.can_modify" with the rights of the caller (an administrator or a
/// member of "zaprett Operators"); a user without them may only read. The interface locks every control that changes
/// the service in advance and never sends such a call, instead of letting the person hit "access_denied".
/// </summary>
public static class ServiceAccess
{
    /// <summary>
    /// The methods any user may call: the same list as ReadOnly of Zaprett.Core CommandDispatcher (a test compares
    /// them). Everything else, including methods of the service itself (tray.autostart), changes something.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnlyMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "status", "items", "strategy.show", "user.get", "check", "repo.list", "presets", "sources.list", "test.status", "job.status",
        "job.log", "probe.status", "monitor.status", "diagnose.status", "dns.status", "log", "diag", "version", "page", "conflicts",
        "settings.get", "update.check",
    };

    public static bool Modifies(string method) => !ReadOnlyMethods.Contains(method);

    /// <summary>
    /// Only an explicit false locks: a service without the field (older than can_modify) or no status yet keeps the
    /// earlier behaviour, where the service itself refuses with access_denied.
    /// </summary>
    public static bool CanModify(JsonObject? status) =>
        status?["can_modify"] is not JsonValue v || v.GetValueKind() != JsonValueKind.False;
}
