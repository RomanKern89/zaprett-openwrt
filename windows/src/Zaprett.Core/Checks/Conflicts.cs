using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Text;
using Zaprett.Core.Util;

namespace Zaprett.Core.Checks;

/// <summary>Conflicts that break the bypass whatever the strategy (severity "block" of <see cref="IConflictScanner"/>): another
/// WinDivert, another winws, GoodbyeDPI. The scanner of the service caches its answer, so status may ask every time.</summary>
public static class Conflicts
{
    public const string Warning = "conflict_blocking";

    /// <summary>Brief blocking items {id, name, detail, kind, service, path, pid}; the last four are null when the scanner does not know
    /// them. A failed scan gives an empty list and a log line: it must not break status or the monitor.</summary>
    public static async Task<JsonArray> BlockingAsync(PlatformServices p, CancellationToken ct) =>
        await TryBlockingAsync(p, ct).ConfigureAwait(false) ?? [];

    /// <summary>Scanners whose last scan failed: a scanner that keeps failing (status, the watchdog every 5 minutes, the
    /// monitor) is reported once per series, not on every call.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IConflictScanner, object> Failing = new();

    /// <summary>As <see cref="BlockingAsync"/>, but null when the scan failed: "unknown" is not "no conflict".</summary>
    public static async Task<JsonArray?> TryBlockingAsync(PlatformServices p, CancellationToken ct)
    {
        JsonArray items;
        try
        {
            items = await p.Conflicts.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (Failing.TryAdd(p.Conflicts, new object()))
                p.Log.Warn(T.S("log.conflicts_scan_failed", e.Message));
            return null;
        }
        Failing.Remove(p.Conflicts);
        var res = new JsonArray();
        foreach (var n in items)
            if (n is JsonObject o && R.Str(o["severity"]) == "block")
                res.Add(new JsonObject
                {
                    ["id"] = R.Str(o["id"]), ["name"] = R.Str(o["name"]), ["detail"] = R.Str(o["detail"]),
                    ["kind"] = R.Str(o["kind"]), ["service"] = R.Str(o["service"]), ["path"] = R.Str(o["path"]), ["pid"] = R.Long(o["pid"]),
                });
        return res;
    }
}
