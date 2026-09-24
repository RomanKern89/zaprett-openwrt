using System.Globalization;
using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Core.Text;

/// <summary>
/// Texts of a conflict found by the service. Its detail and fix are English; for the findings the interface knows
/// (by id and kind) they are built from the interface's own templates with the service, file and process of the item.
/// Item: {id, name, severity?, detail, fix, kind: service|process|driver, service, path, pid} — missing ones null.
/// Unknown findings (VPN adapters, a proxy, anything new) keep the text of the service.
/// </summary>
public static class ConflictText
{
    /// <summary>Findings with a reason text of their own ("Conflict.Why.&lt;id&gt;").</summary>
    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "goodbyedpi", "zapret", "adguard", "killer", "intel-cns", "checkpoint", "smartbyte", "foreign_windivert_user",
    };

    public static string Detail(JsonObject? item)
    {
        if (item == null)
            return "";
        var (id, kind, service, path, pid) = Fields(item);
        if (id == "foreign_windivert" || (kind == "driver" && id != "windivert_registered"))
            return L.F("Conflict.Detail.driver", service ?? "?", path ?? "?");
        if (id == "windivert_registered")
            return L.F("Conflict.Detail.windivert_registered", path ?? service ?? "?");
        if (KnownReasons.Contains(id))
        {
            var why = L.T("Conflict.Why." + id);
            if (kind == "service" && service != null)
                return L.F("Conflict.Detail.service", service, why);
            if (kind == "process")
                return pid is { } p
                    ? L.F("Conflict.Detail.process", path ?? Name(item), p.ToString(CultureInfo.InvariantCulture), why)
                    : L.F("Conflict.Detail.processNoPid", path ?? Name(item), why);
        }
        return item.Str("detail") ?? "";
    }

    /// <param name="withPath">false: the file is shown on its own line next to the advice (home), so the advice
    /// does not repeat it.</param>
    public static string Fix(JsonObject? item, bool withPath = true)
    {
        if (item == null)
            return "";
        var (id, kind, service, path, pid) = Fields(item);
        if (!withPath && path != null)
        {
            if (id == "foreign_windivert" || (kind == "driver" && id != "windivert_registered"))
                return L.T("Conflict.Fix.driverHere");
            if (kind == "process")
                return pid is { } hp ? L.F("Conflict.Fix.processHere", hp.ToString(CultureInfo.InvariantCulture)) : L.T("Conflict.Fix.processHereNoPid");
        }
        if (id == "foreign_windivert" || (kind == "driver" && id != "windivert_registered"))
            return L.F("Conflict.Fix.driver", path ?? service ?? Name(item));
        if (id == "windivert_registered")
            return L.T("Conflict.Fix.windivert_registered");
        if (kind == "process")
            return pid is { } p
                ? L.F("Conflict.Fix.process", path ?? Name(item), p.ToString(CultureInfo.InvariantCulture))
                : L.F("Conflict.Fix.processNoPid", path ?? Name(item));
        // advice follows the kind of the finding, whatever its id: a service is stopped, a process closed, a driver's program removed
        if (kind == "service" && service != null)
            return L.F("Conflict.Fix.service", service);
        return item.Str("fix") ?? "";
    }

    /// <summary>Where the program is: its file (with the process), else its service; empty when unknown.</summary>
    public static string Where(JsonObject? item)
    {
        if (item == null)
            return "";
        var (_, _, service, path, pid) = Fields(item);
        if (path != null)
            return pid is { } p ? L.F("Conflict.Where.PathPid", path, p.ToString(CultureInfo.InvariantCulture)) : L.F("Conflict.Where.Path", path);
        return service != null ? L.F("Conflict.Where.Service", service) : "";
    }

    private static string Name(JsonObject item) => Blank(item.Str("name")) ?? item.Str("id") ?? "?";

    private static (string Id, string? Kind, string? Service, string? Path, long? Pid) Fields(JsonObject item) =>
        (item.Str("id") ?? "", Blank(item.Str("kind")), Blank(item.Str("service")), Blank(item.Str("path")),
            item.Get("pid") is JsonValue ? item.Long("pid") : null);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
