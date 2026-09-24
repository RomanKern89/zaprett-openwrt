using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Core.Text;

/// <summary>
/// Another program that takes the same traffic (another zapret, GoodbyeDPI, a foreign WinDivert driver or a foreign
/// winws running through the loaded driver): the bypass may be "running" while the internet is down, so the interface
/// shows it in red with the program and a concrete fix. The status carries the warning "conflict_blocking" and the
/// list conflicts_blocking: [{id, name, detail, kind, service, path, pid}] (always an array, missing fields null);
/// automatic selection and monitor.run carry the same items. Items of the "conflicts" method also have "severity",
/// and there only "block" counts. Texts come from <see cref="ConflictText"/>.
/// </summary>
public sealed record BlockingConflict(string Id, string Name, string Where, string Advice)
{
    /// <summary>A "block" item of the "conflicts" method, or null for anything else.</summary>
    public static BlockingConflict? From(JsonObject? item) => item?.Str("severity") == "block" ? FromItem(item) : null;

    /// <summary>An item of conflicts_blocking (every item there blocks).</summary>
    public static BlockingConflict? FromItem(JsonObject? item)
    {
        if (item?.Str("id") is not { Length: > 0 } id)
            return null;
        var name = item.Str("name") is { } n && !string.IsNullOrWhiteSpace(n) ? n : id;
        // the home page shows the file on its own line: the advice does not repeat it
        return new BlockingConflict(id, name, ConflictText.Where(item), ConflictText.Fix(item, withPath: false));
    }

    /// <summary>The first blocking conflict of the status; the warning alone (an empty list) still counts.</summary>
    public static BlockingConflict? FromStatus(JsonNode? status)
    {
        if (All(status, "conflicts_blocking").FirstOrDefault() is { } first)
            return first;
        return status.Strings("warnings").Contains("conflict_blocking")
            ? new BlockingConflict("unknown", L.T("Conflict.UnknownProgram"), "", L.T("Conflict.Fix.unknown"))
            : null;
    }

    /// <summary>Every item of a conflicts_blocking list (status, test results, a job result).</summary>
    public static List<BlockingConflict> All(JsonNode? parent, string key) =>
        parent.Objs(key).Select(FromItem).OfType<BlockingConflict>().ToList();
}
